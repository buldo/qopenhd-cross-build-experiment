#!/usr/bin/env -S dotnet --

#:property NoWarn=NU1903
#:package Fallout.Common@10.3.49

using System.Diagnostics;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using static Fallout.Common.IO.HttpTasks;
using static Fallout.Common.Tooling.ProcessTasks;
using static Fallout.Common.Tools.Git.GitTasks;

var tar = ToolResolver.GetPathTool("tar");
var make = ToolResolver.GetPathTool("make");

var rootDirectory = AbsolutePath.Create(Directory.GetCurrentDirectory());
if (!rootDirectory.ContainsFile("build.cs"))
{
    Log("You have to run script from repo root");
    return;
}

var workdir = rootDirectory / "workdir";
var sysrootDir = workdir / "sysroot";
var qtBinDir = workdir / "qt5-bin";

var targetPlatform = new Platform
{
    NameStub = "pi",
    DebianReleaseName = "bullseye",
    Arch = "armhf",
    BuildDeps =
        [
            "openhd-qt",
        ]
};

BuildQtHost();
//CreateSysroot();
//CloneQOpenHd();
//BuildQOpenHd();

static void Log(string msg)
{
    Console.WriteLine(msg);
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

    Log("sysroot created");
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

void BuildQOpenHd()
{

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
        (qtBinDir / "bin" / "rcc").FileExists())
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

    make($"-C qtbase/src/tools/moc install", qtSourcesDir);
    make($"-C qtbase/src/tools/rcc install", qtSourcesDir);
    make($"-C qttools/src/linguist/lrelease install", qtSourcesDir);

    (qtSourcesDir / "qtbase" / "bin" / "qmake").CopyToDirectory(qtBinDir / "bin");
    (qtSourcesDir / "qtbase" / "mkspecs").CopyToDirectory(qtBinDir);

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