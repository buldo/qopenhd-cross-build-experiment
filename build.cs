#!/usr/bin/env -S dotnet --

#:property NoWarn=NU1903
#:package Fallout.Common@10.3.49

using Fallout.Common.IO;
using Fallout.Common.Tooling;
using static Fallout.Common.IO.HttpTasks;
using static Fallout.Common.Tools.Git.GitTasks;

var rootDirectory = AbsolutePath.Create(Directory.GetCurrentDirectory());
if (!rootDirectory.ContainsFile("build.cs"))
{
    Log("You have to run script from repo root");
    return;
}

var workdir = rootDirectory / "workdir";
var sysrootDir = workdir / "sysroot";

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

CreateSysroot();
// var qopenhdDir = workdir / "qopenhd";
// if (qopenhdDir.DirectoryExists())
// {
//     Log("qOpenHD directory already cloned");
// }
// else
// {
//     Log("Cloning qOpenHd");
//     Git("clone --recurse-submodules https://github.com/OpenHD/QOpenHD.git qopenhd", workdir);
// }

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
    var tar = ToolResolver.GetPathTool("tar");
    tar($"--exclude=./dev -xf {tempSysrootFileName} -C {sysrootDir}");

    Log("sysroot created");
}

class Platform
{
    public required string NameStub { get; init; }

    public required string DebianReleaseName { get; init; }

    public required string Arch { get; init; }

    public required string[] BuildDeps { get; init; }
}