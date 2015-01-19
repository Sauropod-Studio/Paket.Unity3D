// --------------------------------------------------------------------------------------
// FAKE build script
// --------------------------------------------------------------------------------------

#r @"packages/FAKE/tools/FakeLib.dll"

open Fake
open Fake.Git
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper
open System
open System.IO
#if MONO
#else
#load "packages/SourceLink.Fake/Tools/Fake.fsx"
open SourceLink
#endif

// --------------------------------------------------------------------------------------
// START TODO: Provide project-specific details below
// --------------------------------------------------------------------------------------

// Information about the project are used
//  - for version and project name in generated AssemblyInfo file
//  - by the generated NuGet package
//  - to run tests and to publish documentation on GitHub gh-pages
//  - for documentation, you also need to edit info in "docs/tools/generate.fsx"

// The name of the project
// (used by attributes in AssemblyInfo, name of a NuGet package and directory in 'src')
let project = "Paket.Unity3D"

// Short summary of the project
// (used as description in AssemblyInfo and as a short summary for NuGet package)
let summary = "Piggy-backs ontop of Paket to add dependencies to Unity3D projects"

// Longer description of the project
// (used as a description for NuGet package; line breaks are automatically cleaned up)
let description = "Piggy-backs ontop of Paket to add dependencies to Unity3D projects"

// List of author names (for NuGet package)
let authors = [ "devboy" ]

// Tags for your project (for NuGet package)
let tags = "nuget, bundler, F#"

// File system information
let solutionFile  = "Paket.Unity3D.sln"

// Pattern specifying assemblies to be tested using NUnit
let testAssemblies = "tests/**/bin/Release/*Tests*.dll"

// Git configuration (used for publishing documentation in gh-pages branch)
// The profile where the project is posted
let gitOwner = "devboy"
let gitHome = "https://github.com/" + gitOwner

// The name of the project on GitHub
let gitName = "Paket.Unity3D"

// The url for the raw files hosted
let gitRaw = environVarOrDefault "gitRaw" "https://raw.github.com/devboy"

// --------------------------------------------------------------------------------------
// END TODO: The rest of the file includes standard build steps
// --------------------------------------------------------------------------------------

// Read additional information from the release notes document
let release = LoadReleaseNotes "RELEASE_NOTES.md"

let genFSAssemblyInfo (projectPath) =
    let projectName = System.IO.Path.GetFileNameWithoutExtension(projectPath)
    let basePath = "src/" + projectName
    let fileName = basePath + "/AssemblyInfo.fs"
    CreateFSharpAssemblyInfo fileName
      [ Attribute.Title (projectName)
        Attribute.Product project
        Attribute.Description summary
        Attribute.Version release.AssemblyVersion
        Attribute.FileVersion release.AssemblyVersion ]

let genCSAssemblyInfo (projectPath) =
    let projectName = System.IO.Path.GetFileNameWithoutExtension(projectPath)
    let basePath = "src/" + projectName + "/Properties"
    let fileName = basePath + "/AssemblyInfo.cs"
    CreateCSharpAssemblyInfo fileName
      [ Attribute.Title (projectName)
        Attribute.Product project
        Attribute.Description summary
        Attribute.Version release.AssemblyVersion
        Attribute.FileVersion release.AssemblyVersion ]

// Generate assembly info files with the right version & up-to-date information
Target "AssemblyInfo" (fun _ ->
  let fsProjs =  !! "src/**/*.fsproj"
  let csProjs = !! "src/**/*.csproj"
  fsProjs |> Seq.iter genFSAssemblyInfo
  csProjs |> Seq.iter genCSAssemblyInfo
)

// --------------------------------------------------------------------------------------
// Clean build results

Target "Clean" (fun _ ->
    CleanDirs ["bin"; "temp"]
)

Target "CleanDocs" (fun _ ->
    CleanDirs ["docs/output"]
)

// --------------------------------------------------------------------------------------
// Build library & test project

Target "Build" (fun _ ->
    !! solutionFile
    |> MSBuildRelease "" "Rebuild"
    |> ignore
)

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner

Target "RunTests" (fun _ ->
    !! testAssemblies
    |> NUnit (fun p ->
        { p with
            DisableShadowCopy = true
            TimeOut = TimeSpan.FromMinutes 20.
            OutputFile = "TestResults.xml" })
)

#if MONO
#else
// --------------------------------------------------------------------------------------
// SourceLink allows Source Indexing on the PDB generated by the compiler, this allows
// the ability to step through the source code of external libraries https://github.com/ctaggart/SourceLink

Target "SourceLink" (fun _ ->
    let baseUrl = sprintf "%s/%s/{0}/%%var2%%" gitRaw (project.ToLower())
    use repo = new GitRepo(__SOURCE_DIRECTORY__)
    !! "src/**/*.fsproj"
    |> Seq.iter (fun f ->
        let proj = VsProj.LoadRelease f
        logfn "source linking %s" proj.OutputFilePdb
        let files = proj.Compiles -- "**/AssemblyInfo.fs"
        repo.VerifyChecksums files
        proj.VerifyPdbChecksums files
        proj.CreateSrcSrv baseUrl repo.Revision (repo.Paths files)
        Pdbstr.exec proj.OutputFilePdb proj.OutputFilePdbSrcSrv
    )
)
#endif

Target "MergeExe" (fun _ ->
    CreateDir "bin/merge"

    let toPack =
        ["paket.unity3d.exe"; "FSharp.Core.dll"; "Paket.Core.dll"; "Ionic.Zip.dll"; "Newtonsoft.Json.dll";]
        |> List.map (fun l -> "bin/" @@ l)
        |> separated " "

    let result =
        ExecProcess (fun info ->
            info.FileName <- currentDirectory @@ "tools" @@ "ILRepack" @@ "ILRepack.exe"
            info.Arguments <- sprintf "/internalize /verbose /lib:%s /ver:%s /out:%s %s" "bin" release.AssemblyVersion ("bin/merge" @@ "paket.unity3d.exe") toPack
            ) (TimeSpan.FromMinutes 5.)

    if result <> 0 then failwithf "Error during ILRepack execution."
)

// --------------------------------------------------------------------------------------
// Build a NuGet package

let publishNugetParam =
  #if MONO
  false
  #else
  hasBuildParam "nugetkey"
  #endif

let publishNuget nugetParams =
  NuGet (fun p -> nugetParams) ("nuget/" + nugetParams.Project + ".nuspec")
#if MONO
  if hasBuildParam "nugetkey" then
    let source =
      if isNullOrEmpty nugetParams.PublishUrl then ""
      else sprintf "-s %s" nugetParams.PublishUrl
    let args = sprintf "push \"%s\" %s %s" (nugetParams.OutputPath @@ sprintf "%s.%s.nupkg" nugetParams.Project nugetParams.Version) nugetParams.AccessKey source
    let result =
      ExecProcess (fun info ->
        info.FileName <- nugetParams.ToolPath
        info.WorkingDirectory <- FullName nugetParams.WorkingDir
        info.Arguments <- args) nugetParams.TimeOut
    if result <> 0 then failwithf "Error during NuGet push. %s %s" nugetParams.ToolPath args
#endif


Target "NuGet->Tool" (fun _ ->
    { NuGetHelper.NuGetDefaults() with
            Authors = authors
            Project = project
            Summary = summary
            Description = description
            Version = release.NugetVersion
            ReleaseNotes = String.Join(Environment.NewLine, release.Notes)
            WorkingDir = "."
            Tags = tags
            OutputPath = "bin"
            AccessKey = getBuildParamOrDefault "nugetkey" ""
            Publish = publishNugetParam
            Dependencies = [] }
    |> publishNuget
)

Target "NuGet->Example" (fun _ ->
  { NuGetHelper.NuGetDefaults() with
        Authors = authors
        Project = project + ".Example.Source"
        Summary = "Example project demonstrating Paket.Unity3D"
        Description = "Example project demonstrating Paket.Unity3D"
        Version = release.NugetVersion
        ReleaseNotes = String.Join(Environment.NewLine, release.Notes)
        WorkingDir = "."
        Tags = tags
        OutputPath = "bin"
        AccessKey = getBuildParamOrDefault "nugetkey" ""
        Publish = publishNugetParam
        Dependencies = [] }
  |> publishNuget
)

Target "NuGet" DoNothing

// --------------------------------------------------------------------------------------
// Generate the documentation

Target "GenerateReferenceDocs" (fun _ ->
    if not <| executeFSIWithArgs "docs/tools" "generate.fsx" ["--define:RELEASE"; "--define:REFERENCE"] [] then
      failwith "generating reference documentation failed"
)

let generateHelp fail =
    if executeFSIWithArgs "docs/tools" "generate.fsx" ["--define:RELEASE"; "--define:HELP"] [] then
        traceImportant "Help generated"
    else
        if fail then
            failwith "generating help documentation failed"
        else
            traceImportant "generating help documentation failed"


Target "GenerateHelp" (fun _ ->
    DeleteFile "docs/content/release-notes.md"
    CopyFile "docs/content/" "RELEASE_NOTES.md"
    Rename "docs/content/release-notes.md" "docs/content/RELEASE_NOTES.md"

    DeleteFile "docs/content/license.md"
    CopyFile "docs/content/" "LICENSE.txt"
    Rename "docs/content/license.md" "docs/content/LICENSE.txt"

    generateHelp true
)


Target "KeepRunning" (fun _ ->
    use watcher = new FileSystemWatcher(DirectoryInfo("docs/content").FullName,"*.*")
    watcher.EnableRaisingEvents <- true
    watcher.Changed.Add(fun e -> generateHelp false)
    watcher.Created.Add(fun e -> generateHelp false)
    watcher.Renamed.Add(fun e -> generateHelp false)
    watcher.Deleted.Add(fun e -> generateHelp false)

    traceImportant "Waiting for help edits. Press any key to stop."

    System.Console.ReadKey() |> ignore

    watcher.EnableRaisingEvents <- false
    watcher.Dispose()
)

Target "GenerateDocs" DoNothing

// --------------------------------------------------------------------------------------
// Release Scripts

Target "ReleaseDocs" (fun _ ->
    let tempDocsDir = "temp/gh-pages"
    CleanDir tempDocsDir
    Repository.cloneSingleBranch "" (gitHome + "/" + gitName + ".git") "gh-pages" tempDocsDir

    fullclean tempDocsDir
    CopyRecursive "docs/output" tempDocsDir true |> tracefn "%A"
    StageAll tempDocsDir
    Git.Commit.Commit tempDocsDir (sprintf "Update generated documentation for version %s" release.NugetVersion)
    Branches.push tempDocsDir
)

#load "paket-files/fsharp/FAKE/modules/Octokit/Octokit.fsx"
open Octokit

Target "Release" (fun _ ->
    StageAll ""
    Git.Commit.Commit "" (sprintf "Bump version to %s" release.NugetVersion)
    Branches.push ""

    Branches.tag "" release.NugetVersion
    Branches.pushTag "" "origin" release.NugetVersion

    // release on github
    createClient (getBuildParamOrDefault "github-user" "") (getBuildParamOrDefault "github-pw" "")
    |> createDraft gitOwner gitName release.NugetVersion (release.SemVer.PreRelease <> None) release.Notes
    |> uploadFile "bin/merge/paket.unity3d.exe"
    |> releaseDraft
    |> Async.RunSynchronously
)

Target "BuildPackage" DoNothing

// --------------------------------------------------------------------------------------
// Run all targets by default. Invoke 'build <Target>' to override

Target "All" DoNothing

"Clean"
  ==> "AssemblyInfo"
  ==> "Build"
  ==> "RunTests"
  =?> ("GenerateReferenceDocs",isLocalBuild && not isMono)
  =?> ("GenerateDocs",isLocalBuild && not isMono)
  ==> "All"
  =?> ("ReleaseDocs",isLocalBuild && not isMono)

"All"
#if MONO
#else
  =?> ("SourceLink", Pdbstr.tryFind().IsSome )
#endif
  ==> "MergeExe"
  ==> "NuGet"
  ==> "BuildPackage"

"NuGet->Tool"
  ==> "NuGet->Example"
  ==> "NuGet"

"CleanDocs"
  ==> "GenerateHelp"
  ==> "GenerateReferenceDocs"
  ==> "GenerateDocs"

"GenerateHelp"
  ==> "KeepRunning"

"ReleaseDocs"
  ==> "Release"

"BuildPackage"
  ==> "Release"

RunTargetOrDefault "All"