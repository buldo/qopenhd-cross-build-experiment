#!/usr/bin/env -S dotnet --

#:package Fallout.Common@10.3.49

using Fallout.Common.IO;
using Fallout.Common.Tooling;
using static Fallout.Common.IO.HttpTasks;

string sysrootFileUrl = "https://dl.cloudsmith.io/public/openhd/dev-release/raw/files/openhd-sysroot-bullseye-arm64.tar.zst";

Console.WriteLine("Start building");

var workdir = AbsolutePath.Create(Directory.GetCurrentDirectory()) / "workdir";
var sysrootFilePath = workdir / sysrootFileUrl.Split('/').Last();

if (sysrootFilePath.FileExists())
{
    Log("Sysroot file exists. Skipping downloading");
}
else
{
    Log("Downloading sysroot");
    await HttpDownloadFileAsync(sysrootFileUrl, sysrootFilePath, FileMode.Create, c => {c.Timeout = TimeSpan.FromMinutes(10); return c;});
    Log("Sysroot downloaded");
}

var sysrootDir = workdir / "sysroot";
sysrootDir.CreateOrCleanDirectory();

var tar = ToolResolver.GetPathTool("tar");
tar($"--exclude=./dev -xf {sysrootFilePath.Name} -C {sysrootDir}", workdir);

static void Log(string msg)
{
    Console.WriteLine(msg);
}