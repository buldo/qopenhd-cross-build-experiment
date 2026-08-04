#!/usr/bin/env -S dotnet --

#:property NoWarn=NU1903
#:package Fallout.Common@10.3.49

using System.Diagnostics;
using System.Linq;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using static Fallout.Common.IO.HttpTasks;
using static Fallout.Common.Tooling.ProcessTasks;
using static Fallout.Common.Tools.Git.GitTasks;

var tar = ToolResolver.GetPathTool("tar");
var make = ToolResolver.GetPathTool("make");
var dpkgDeb = ToolResolver.GetPathTool("dpkg-deb");

var rootDirectory = AbsolutePath.Create(Directory.GetCurrentDirectory());
if (!rootDirectory.ContainsFile("build.cs"))
{
    Log("You have to run script from repo root");
    return;
}

var workdir = rootDirectory / "workdir";
var sysrootDir = workdir / "sysroot";
var qtBinDir = workdir / "qt5-bin";
var gccDir = workdir / "gcc-armhf";

// Debian package names that provide the arm-linux-gnueabihf dev headers/.so/.pc files QOpenHD links against
var targetPlatform = new Platform
{
    NameStub = "pi",
    DebianReleaseName = "bullseye",
    Arch = "armhf",
    BuildDeps =
        [
            "openhd-qt",
            "libc6-dev",
            "libavcodec-dev",
            "libavformat-dev",
            "libavutil-dev",
            "libgstreamer1.0-dev",
            "libgstreamer-plugins-base1.0-dev",
            "libdrm-dev",
            "libgles-dev",
            "libegl-dev",
        ]
};

PrepareGcc();
BuildQtHost();
CreateSysroot();
CloneQOpenHd();
ConfigureCrossQmake();
BuildQOpenHd();
PackageQOpenHd();

static void Log(string msg)
{
    Console.WriteLine(msg);
}

void PrepareGcc()
{
    var gccDownloadUrl = "https://toolchains.bootlin.com/downloads/releases/toolchains/armv7-eabihf/tarballs/armv7-eabihf--glibc--bleeding-edge-2020.08-1.tar.bz2";
    var archiveName = "armv7-eabihf--glibc--bleeding-edge-2020.08-1.tar.bz2";
    var archivePath = workdir / archiveName;

    if (archivePath.FileExists())
    {
        Log("GCC: Toolchain archive already downloaded");
    }
    else
    {
        Log("GCC: Downloading toolchain");
        HttpDownloadFile(gccDownloadUrl, archivePath, FileMode.Create, c => { c.Timeout = TimeSpan.FromMinutes(30); return c; });
        Log("GCC: Toolchain downloaded");
    }

    if (gccDir.DirectoryExists())
    {
        Log("GCC: Toolchain already extracted");
    }
    else
    {
        Log("GCC: Extracting toolchain");
        gccDir.CreateDirectory();
        tar($"xf {archivePath} -C {gccDir} --strip-components=1", workdir);
        Log("GCC: Toolchain extracted");
    }
}

void CreateSysroot()
{
    Log($"Creating sysroot for {sysrootDir.Name}");
    if (sysrootDir.DirectoryExists())
    {
        Log("Sysroot directory already exists");
        return;
    }

    Log($"Running mmdebstrap for {sysrootDir.Name}");
    var sourcesFile = rootDirectory / $"{targetPlatform.NameStub}-{targetPlatform.DebianReleaseName}-{targetPlatform.Arch}.sources.list";
    if (!sourcesFile.FileExists())
    {
        throw new FileNotFoundException($"Source file {sourcesFile} not found");
    }

    var tempSysrootFileName = workdir / "sysroot.tar";
    tempSysrootFileName.DeleteFile();

    string[] debstrapArgsArray =
    [
        "--mode=unshare",
        $"--architectures={targetPlatform.Arch}",
        "--variant=extract",
        $"--include={string.Join(',',targetPlatform.BuildDeps)}",
        $"{targetPlatform.DebianReleaseName}",
        $"{tempSysrootFileName}",
        $"{sourcesFile}",
        "-v"
        ];
    var debstrapArgs = string.Join(' ', debstrapArgsArray);
    Log($"Calling {debstrapArgs}");

    var mmdebstrap = ToolResolver.GetPathTool("mmdebstrap");
    mmdebstrap(debstrapArgs);

    sysrootDir.CreateDirectory();
    tar($"--exclude=./dev -xf {tempSysrootFileName} -C {sysrootDir}");

    FixupAbsoluteSymlinks(sysrootDir);

    Log("sysroot created");
}

void FixupAbsoluteSymlinks(AbsolutePath root)
{
    // Debian packages use absolute symlinks (e.g. libpthread.so -> /lib/arm-linux-gnueabihf/libpthread.so.0)
    // which resolve against the host's real root, not the sysroot; rewrite them as sysroot-relative links
    Log("Sysroot: rewriting absolute symlinks as relative");
    var rootPath = (string)root;
    var fixedCount = 0;
    foreach (var entryPath in Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.AllDirectories))
    {
        var linkTarget = new FileInfo(entryPath).LinkTarget;
        if (linkTarget is not { } target || !target.StartsWith('/'))
        {
            continue;
        }

        var realTarget = rootPath + target;
        var relativeTarget = Path.GetRelativePath(Path.GetDirectoryName(entryPath)!, realTarget);
        File.Delete(entryPath);
        File.CreateSymbolicLink(entryPath, relativeTarget);
        fixedCount++;
    }

    Log($"Sysroot: rewrote {fixedCount} absolute symlink(s)");
}

void CloneQOpenHd()
{
    var qopenhdDir = workdir / "qopenhd";
    if (qopenhdDir.DirectoryExists())
    {
        Log("qOpenHD directory already cloned");
    }
    else
    {
        Log("Cloning qOpenHd");
        Git("clone --recurse-submodules https://github.com/OpenHD/QOpenHD.git qopenhd", workdir);
    }
}

// Custom qmake device spec name; must match one of the names platforms.pri checks for a LinuxBuild/eglfs target
const string DeviceSpecName = "linux-rpi4-v3d-g++";

void ConfigureCrossQmake()
{
    Log("Cross Qt: Configuring qt.conf and device mkspec");

    var targetQtPrefix = "/opt/Qt5.15.4";
    var targetQtDataDir = sysrootDir + targetQtPrefix;
    var crossCompilePrefix = $"{gccDir}/bin/arm-buildroot-linux-gnueabihf-";
    var deviceSpecDir = sysrootDir / "opt" / "Qt5.15.4" / "mkspecs" / "devices" / DeviceSpecName;

    // Redirects the host-only qmake to the target Qt install inside the sysroot (Sysroot + SysrootifyPrefix),
    // while keeping moc/rcc/qmake/lrelease themselves resolved from the host build in qtBinDir
    (qtBinDir / "bin" / "qt.conf").WriteAllText(
        "[Paths]\n" +
        $"Sysroot={sysrootDir}\n" +
        "SysrootifyPrefix=true\n" +
        $"Prefix={targetQtPrefix}\n" +
        $"HostPrefix={qtBinDir}\n" +
        $"HostData={targetQtDataDir}\n" +
        $"HostBinaries={qtBinDir}/bin\n");

    deviceSpecDir.CreateDirectory();

    // Modeled on qtbase's devices/linux-rasp-pi4-v3d-g++ (Mesa v3d/EGL, no legacy /opt/vc broadcom stack),
    // pointed at our bootlin toolchain and the Debian sysroot instead of the toolchain's own bundled one
    (deviceSpecDir / "qmake.conf").WriteAllText(
        "# Auto-generated by build.cs - do not edit by hand\n" +
        $"CROSS_COMPILE = {crossCompilePrefix}\n" +
        "\n" +
        "include(../common/linux_device_pre.conf)\n" +
        "\n" +
        "QMAKE_LIBS_EGL         += -lEGL\n" +
        "QMAKE_LIBS_OPENGL_ES2  += -lGLESv2 -lEGL\n" +
        "\n" +
        "QMAKE_CFLAGS           += --sysroot=$$[QT_SYSROOT]\n" +
        "QMAKE_CXXFLAGS         += --sysroot=$$[QT_SYSROOT]\n" +
        "QMAKE_LFLAGS           += --sysroot=$$[QT_SYSROOT]\n" +
        "\n" +
        "# Toolchain is not multiarch-aware; point it at Debian's arm-linux-gnueabihf lib dir for crt*.o and libs\n" +
        "QMAKE_LFLAGS           += -B$$[QT_SYSROOT]/usr/lib/arm-linux-gnueabihf\n" +
        "QMAKE_LFLAGS           += -L$$[QT_SYSROOT]/usr/lib/arm-linux-gnueabihf\n" +
        "QMAKE_LFLAGS           += -L$$[QT_SYSROOT]/lib/arm-linux-gnueabihf\n" +
        "\n" +
        "DISTRO_OPTS            += hard-float\n" +
        "DISTRO_OPTS            += deb-multi-arch\n" +
        "\n" +
        "EGLFS_DEVICE_INTEGRATION = eglfs_kms\n" +
        "\n" +
        "include(../common/linux_arm_device_post.conf)\n" +
        "\n" +
        "# Toolchain only ships an unprefixed pkg-config wrapper; PKG_CONFIG_* env vars redirect it to our sysroot\n" +
        "QMAKE_PKG_CONFIG        = pkg-config\n" +
        "\n" +
        "load(qt_config)\n");

    (deviceSpecDir / "qplatformdefs.h").WriteAllText(
        "#include \"../../linux-g++/qplatformdefs.h\"\n");

    Log("Cross Qt: qt.conf and device mkspec written");
}

void BuildQOpenHd()
{
    var qopenhdDir = workdir / "qopenhd";
    var buildDir = qopenhdDir / "build-armhf";
    buildDir.CreateDirectory();

    var envVars = new Dictionary<string, string>
    {
        ["PATH"] = $"{qtBinDir}/bin:{gccDir}/bin:{Environment.GetEnvironmentVariable("PATH")}",
        // Toolchain's pkg-config wrapper defaults to its own bundled sysroot; override it to use the Debian sysroot
        ["PKG_CONFIG_SYSROOT_DIR"] = $"{sysrootDir}",
        ["PKG_CONFIG_LIBDIR"] = $"{sysrootDir}/usr/lib/arm-linux-gnueabihf/pkgconfig:{sysrootDir}/usr/lib/pkgconfig:{sysrootDir}/usr/share/pkgconfig",
    };

    var qmake = ToolResolver.GetTool(qtBinDir / "bin" / "qmake");
    qmake(
        $"{qopenhdDir}/QOpenHD.pro -spec devices/{DeviceSpecName} CONFIG+=release",
        buildDir,
        environmentVariables: envVars);

    make($"-j{Environment.ProcessorCount}", buildDir, environmentVariables: envVars);
}

void PackageQOpenHd()
{
    Log("Package: Building deb package");

    var qopenhdDir = workdir / "qopenhd";
    var releaseBinary = qopenhdDir / "build-armhf" / "release" / "QOpenHD";
    if (!releaseBinary.FileExists())
    {
        throw new FileNotFoundException($"Built binary not found at {releaseBinary}");
    }

    const string packageName = "qopenhd";
    const string arch = "armhf";

    var pkgRoot = workdir / "qopenhd-pkg";
    if (pkgRoot.DirectoryExists())
    {
        pkgRoot.DeleteDirectory();
    }

    var binDir = pkgRoot / "usr" / "local" / "bin";
    var systemdDir = pkgRoot / "etc" / "systemd" / "system";
    var shareDir = pkgRoot / "usr" / "local" / "share" / "qopenhd";
    var debianDir = pkgRoot / "DEBIAN";
    binDir.CreateDirectory();
    systemdDir.CreateDirectory();
    shareDir.CreateDirectory();
    debianDir.CreateDirectory();

    releaseBinary.CopyToDirectory(binDir, ExistsPolicy.FileOverwrite);
    (qopenhdDir / "systemd" / "h264_decode.service").CopyToDirectory(systemdDir, ExistsPolicy.FileOverwrite);
    (qopenhdDir / "systemd" / "h265_decode.service").CopyToDirectory(systemdDir, ExistsPolicy.FileOverwrite);
    File.Copy(qopenhdDir / "systemd" / "rpi_qopenhd.service", systemdDir / "qopenhd.service", overwrite: true);
    File.Copy(qopenhdDir / "rpi_qt_eglfs_kms_config.json", shareDir / "rpi_qt_eglfs_kms_config.json", overwrite: true);

    var gitHash = Git("rev-parse --short HEAD", qopenhdDir)
        .First(o => o.Type == OutputType.Std).Text.Trim();
    var version = $"2.7.1-{DateTime.Now:MM-dd-yyyy--HH-mm-ss}-{gitHash}";

    // Mirrors package.sh's bullseye/raspbian/armhf dependency set
    string[] depends =
        [
            "openhd-userland",
            "libavcodec-dev",
            "libavformat-dev",
            "openhd-qt",
            "gst-plugins-good",
            "gst-openhd-plugins",
            "gstreamer1.0-gl",
        ];

    (debianDir / "control").WriteAllText(
        $"Package: {packageName}\n" +
        $"Version: {version}\n" +
        "Section: base\n" +
        "Priority: optional\n" +
        $"Architecture: {arch}\n" +
        $"Depends: {string.Join(", ", depends)}\n" +
        "Maintainer: OpenHD\n" +
        "Description: QOpenHD ground station application\n");

    File.Copy(qopenhdDir / "after-install.sh", debianDir / "postinst", overwrite: true);
    File.SetUnixFileMode(
        debianDir / "postinst",
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

    var debFile = workdir / $"{packageName}_{version}_{arch}.deb";
    debFile.DeleteFile();
    dpkgDeb($"--build --root-owner-group {pkgRoot} {debFile}");

    Log($"Package: created {debFile}");
}

void BuildQtHost()
{
    Log("Qt: Preparing");
    var suffix = ".tar.xz";
    var qtVersion = "5.15.4";
    var archiveName = $"qt-everywhere-opensource-src-{qtVersion}{suffix}";
    var archivePath = workdir / archiveName;
    if (archivePath.FileExists())
    {
        Log("Qt: Sources are already downloaded");
    }
    else
    {
        Log("Qt: Source archive not exists. Downloading");
        var downloadPath = $"https://download.qt.io/archive/qt/5.15/{qtVersion}/single/{archiveName}";
        HttpDownloadFile(downloadPath, workdir / archiveName, FileMode.Create, c => { c.Timeout = TimeSpan.FromMinutes(30); return c; });
        Log("Qt: Sources downloaded");
    }

    var qtSourcesDir = workdir / $"qt-everywhere-src-{qtVersion}";
    if (qtSourcesDir.DirectoryExists())
    {
        Log("Qt: Sources already unpacked");
    }
    else
    {
        Log("Qt: Unpacking sources");
        tar($"xf {archiveName}", workdir);
        Log("Qt: Sources unpacked");
    }

    if ((qtBinDir / "bin" / "lrelease").FileExists() &&
        (qtBinDir / "bin" / "moc").FileExists() &&
        (qtBinDir / "bin" / "qmake").FileExists() &&
        (qtBinDir / "bin" / "rcc").FileExists() &&
        (qtBinDir / "bin" / "qmltyperegistrar").FileExists())
    {
        Log("Qt: all binaries exist. Done.");
        return;
    }

    Log("Qt: Configure");
    Sed(
        "#include <QtCore/qbytearray.h>",
        "#include <QtCore/qbytearray.h>\n#include <limits>",
        qtSourcesDir / "qtbase" / "src" / "corelib" / "text" / "qbytearraymatcher.h");

    StartShell(
        $"./configure -prefix {qtBinDir} -opensource -confirm-license -release " +
        "-optimize-size -nomake examples -nomake tests -no-gui -no-widgets -no-dbus -no-opengl -no-openssl -no-icu " +
        "-qt-pcre -qt-zlib -qt-doubleconversion -static",
        qtSourcesDir)
        .AssertZeroExitCode();
    Log("Qt: Building");

    var procCount = Environment.ProcessorCount;
    make($"-j{procCount} module-qtbase-qmake_all", qtSourcesDir);

    make($"-j{procCount} -C qtbase/src sub-bootstrap", qtSourcesDir);
    make($"-j{procCount} -C qtbase/src sub-moc sub-rcc", qtSourcesDir);
    make($"-j{procCount} -C qtbase/src sub-corelib sub-xml", qtSourcesDir);

    var qmake = ToolResolver.GetTool(qtSourcesDir / "qtbase" / "bin" / "qmake");
    qmake("", qtSourcesDir / "qttools");
    qmake("", qtSourcesDir / "qttools" / "src" / "linguist" / "lrelease");
    make($"-j{procCount}", qtSourcesDir / "qttools" / "src" / "linguist" / "lrelease");

    // Required at target-app build time by CONFIG += qmltypes (used by QOpenHD.pro)
    qmake("", qtSourcesDir / "qtdeclarative" / "src" / "qmltyperegistrar");
    make($"-j{procCount}", qtSourcesDir / "qtdeclarative" / "src" / "qmltyperegistrar");

    make($"-C qtbase/src/tools/moc install", qtSourcesDir);
    make($"-C qtbase/src/tools/rcc install", qtSourcesDir);
    make($"-C qttools/src/linguist/lrelease install", qtSourcesDir);
    make($"-C qtdeclarative/src/qmltyperegistrar install", qtSourcesDir);

    (qtSourcesDir / "qtbase" / "bin" / "qmake").CopyToDirectory(qtBinDir / "bin", ExistsPolicy.FileOverwrite);
    (qtSourcesDir / "qtbase" / "mkspecs").CopyToDirectory(qtBinDir, ExistsPolicy.MergeAndOverwrite);

    // For cross build safety
    (qtBinDir / "mkspecs" / "qconfig.pri").DeleteFile();
    (qtBinDir / "mkspecs" / "qmodule.pri").DeleteFile();
    (qtBinDir / "mkspecs" / "qdevice.pri").DeleteFile();

    Log("Qt: Building done");
}

void Sed(string original, string replace, string path)
{
    var text = File.ReadAllText(path);
    if (text.Contains(replace))
    {
        return;
    }

    text = text.Replace(original, replace);
    File.WriteAllText(path, text);
}

class Platform
{
    public required string NameStub { get; init; }

    public required string DebianReleaseName { get; init; }

    public required string Arch { get; init; }

    public required string[] BuildDeps { get; init; }
}