#r @"packages/build/FAKE/tools/FakeLib.dll"

open Fake
open Fake.AssemblyInfoFile
open Fake.ProcessHelper

open System
open System.IO
open System.Text.RegularExpressions

(*################################# Definitions #################################*)

type GitVersionInfo = {
  Major: int
  Minor: int
  Patch: int
  CommitsAhead : int
  IsPreRelease: bool
  IsReleaseCandidate : bool
  PreReleaseTag: string
  PreReleaseVersion: int
  Hash: string
  Sha: string
  Branch: string
  LastReleaseTag: string
}

let CreateAssemblyVersion info =
  sprintf "%i.%i.%i.%i" info.Major info.Minor info.Patch info.CommitsAhead

let CreateSemVer info =
  if info.IsPreRelease
    then (sprintf "%i.%i.%i-%s%i" info.Major info.Minor info.Patch info.PreReleaseTag info.PreReleaseVersion)
    else (sprintf "%i.%i.%i" info.Major info.Minor info.Patch)

let CreateInformationalVersion info =
  sprintf "%s+%i.Branch.%s.Sha.%s" (CreateSemVer info) info.CommitsAhead info.Branch info.Sha

let GetVersionInfo tag_prefix =
  let sha = Git.Information.getCurrentSHA1 ""
  let last_tag = Git.CommandHelper.runSimpleGitCommand "" (sprintf "describe --tags --abbrev=0 HEAD --always --match \"%s[0-9]*.[0-9]*\"" tag_prefix)
  let last_release_tag = if last_tag <> sha then last_tag else ""

  let rex_match = Regex.Match(last_release_tag, "(?<version>\d+\.\d+(\.\d+)?)(-(?<prerelease>[0-9A-Za-z-]*)(\.(?<preversion>\d+))?)?")

  let version = if rex_match.Success then Version.Parse rex_match.Groups.["version"].Value else new Version(0,0,0)

  let branch = Git.CommandHelper.runSimpleGitCommand "" "rev-parse --abbrev-ref HEAD"
  
  let pre_release_tag_group = rex_match.Groups.["prerelease"]
  let pre_release_tag = 
    if pre_release_tag_group.Success 
    then Some(pre_release_tag_group.Value) 
    else
      match branch with
      | "master" -> None
      | "hotfix" -> Some("rc")
      | "release" -> Some("rc")
      | "develop" -> Some("beta")
      | _ -> Some("alpha")
      
  let isReleaseCandidate = pre_release_tag = Some("rc")
      
  let commits_ahead =
    if isReleaseCandidate
      then 0
      else if last_tag <> sha then Git.Branches.revisionsBetween "" last_tag sha else int (Git.CommandHelper.runSimpleGitCommand "" "rev-list HEAD --count")

  let pre_release_version_group = rex_match.Groups.["preversion"]
  let pre_release_version = if pre_release_version_group.Success then int pre_release_version_group.Value else commits_ahead

  { Major = version.Major
    Minor = version.Minor
    Patch = if version.Build <> -1 then version.Build else 0
    CommitsAhead = commits_ahead
    IsPreRelease = pre_release_tag.IsSome
    IsReleaseCandidate = isReleaseCandidate
    PreReleaseTag = pre_release_tag |> function | None -> "" | Some s -> s
    PreReleaseVersion = pre_release_version
    Hash = Git.Information.getCurrentHash()
    Sha = sha
    Branch = branch
    LastReleaseTag = last_release_tag}
    
let IsPublishAllowed info =
    (info.Branch = "master" && info.CommitsAhead = 0) || (info.Branch = "integration" && info.CommitsAhead <> 0)

(*################################# Assign Variables #################################*)

let solutionFile  = "##ProjectName##.sln"
let testAssemblies = "tests/**/bin/Release/*Tests*.dll"

let versionInfo = GetVersionInfo "release/*"

let dirBuildOutput = "_build.output"
let dirTestsOutput = dirBuildOutput @@ "tests"
let dirDocOutput = dirBuildOutput @@ "docs"
let dirNuGetOutput = dirBuildOutput @@ "nuget"
let dirBinOutput = dirBuildOutput @@ "bin"

let buildConfiguration = "Release"

(*################################# Print Variables #################################*)

traceHeader "Variables"
trace <| sprintf "%A" versionInfo
traceHeader ""

(*################################# Tasks #################################*)

Target "AssemblyInfo" (fun _ ->
  
  let assemblyVersion = CreateAssemblyVersion versionInfo
  let informationalVersion = CreateInformationalVersion versionInfo

  trace <| sprintf "AssemblyVersion: %s" assemblyVersion
  trace <| sprintf "AssemblyInformationalVersion: %s" informationalVersion
  
  !! ("src/**/*Info.cs")
    |> Seq.iter(fun file ->
       (ReplaceAssemblyInfoVersions (fun f ->
          { f with
              AssemblyInformationalVersion = informationalVersion
              AssemblyVersion = assemblyVersion
              OutputFileName = file })
        
        fireAndForgetGitCommand "" ("update-index --assume-unchanged " + file)
        )
    )
)

Target "CopyBinaries" (fun _ ->
  !! "src/**/*.??proj"
  -- "src/**/*.shproj"
  |>  Seq.map (fun f -> ((System.IO.Path.GetDirectoryName f) </> "bin/Release", dirBuildOutput @@ "bin" </> (System.IO.Path.GetFileNameWithoutExtension f)))
  |>  Seq.iter (fun (fromDir, toDir) -> CopyDir toDir fromDir (fun _ -> true))
)

Target "Clean" (fun _ ->
  CleanDirs [dirBuildOutput]
)

Target "CleanDocs" (fun _ ->
  CleanDirs [ dirBuildOutput @@ "docs"]
)

Target "Build" (fun _ ->
  MSBuildDefaults  <- { MSBuildDefaults with Verbosity = Some(MSBuildVerbosity.Quiet) }
  sprintf "building %s -> %s" solutionFile buildConfiguration |> traceHeader
  MSBuild "" "Build" ["Configuration", buildConfiguration] [solutionFile] |> ignore
)

Target "RunTests" (fun _ ->
  let tests = !! testAssemblies
  if not (tests |> Seq.isEmpty)
  then  
    ensureDirectory dirTestsOutput
    tests
      |> NUnit (fun p -> 
        { p with
            ShowLabels = false
            ErrorLevel = DontFailBuild
            DisableShadowCopy = true
            TimeOut = (TimeSpan.FromMinutes 10.)
            Domain = MultipleDomainModel
            ProcessModel = MultipleProcessModel
            OutputFile = (dirTestsOutput @@ "nunit-report.xml")
            })
            
    PublishArtifact dirTestsOutput
)

Target "NuGet" (fun _ ->
  Paket.Pack(fun p ->
      {p with
          Version = (CreateSemVer versionInfo)
          OutputPath = dirNuGetOutput
          })
          
  PublishArtifact dirNuGetOutput
)

Target "NuGetPush" (fun _ ->
  if not (IsPublishAllowed versionInfo)
  then failwithf "Vom aktuellen Stand darf keine keine Lieferung verteilt werden. %A" versionInfo
  
  let nugetPublishUrl = environVarOrFail "NuGetPublishUrl"
  let nugetApiKey = environVarOrFail "nugetkey"
  
  Paket.Push(fun p ->
      {p with
          WorkingDir = (dirBuildOutput @@ "nuget")
          PublishUrl = nugetPublishUrl
          ApiKey = nugetApiKey
          DegreeOfParallelism = 1
          EndPoint = "api/packages"
          })
)

Target "GenerateDocs" (fun _ ->
  traceHeader "GenerateDocs"
  
  let dirDocs = "docs"
  
  let result =
    ExecProcess (fun info ->
      info.FileName <- currentDirectory @@ "packages" @@ "build" @@ "docfx.msbuild" @@ "tools" @@ "docfx.exe"
      info.Arguments <- sprintf "%s" (dirDocs @@ "docfx.json")
      ) (TimeSpan.FromMinutes 5.)

  if result <> 0 then failwithf "Error during docfx execution."
  
  let testResults = dirTestsOutput @@ "nunit-report.xml"
  if (fileExists testResults)
  then
    let result =
      ExecProcess (fun info ->
        info.FileName <- currentDirectory @@ "packages" @@ "build" @@ "NUnit2Report.Console.Runner" @@ "NUnit2Report.Console.exe"
        info.Arguments <- sprintf "--fileset=\"%s\" --todir \"%s\"" testResults (dirDocOutput @@ "testreport")
        ) (TimeSpan.FromMinutes 15.)
        
    if result <> 0 then failwithf "Error during NUnit2Report execution."
  
  PublishArtifact dirDocOutput
)

Target "?" (fun _ ->
  traceHeader "target summary"
  trace ""
  listTargets  ()
  trace ""
)

Target "Publish" DoNothing
Target "All" DoNothing

"Clean"
  ==> "AssemblyInfo"
  ==> "Build"
  ==> "CopyBinaries"
  ==> "RunTests"
  ==> "GenerateDocs"
  ==> "All"

"All"
  ==> "NuGet"
  =?> ("NuGetPush", isLocalBuild || (IsPublishAllowed versionInfo))
  ==> "Publish"

"CleanDocs"
  ==> "GenerateDocs"


RunTargetOrDefault "All"