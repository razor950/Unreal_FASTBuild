# Unreal_FASTBuild
Allows the usage of FASTBuild with Unreal Engine 4 with a few minor modifications. It supports Visual Studio 2015 and 2017 and the Windows 10 SDK, UE4 4.21.1 and FASTBuild v0.95, although you should be able to use this with previous versions of UE4 by looking at the history of the file.

To use, place the under Engine/Source/Programs/UnrealBuildTool/System/ and add to the UnrealBuildTool project or regenerate the projects. You can then call it from ExecuteActions() in ActionGraph.cs a similar manner to XGE, for 4.21.1 it will look something like it does in the gist here: https://gist.github.com/liamkf/9e8a660be117c85428054fe76dfd5eff

It requires FBuild.exe to be in your path or modifying FBuildExePathOverride to point to where your FBuild executable is.

One example of the diffs and how to use it was made available here https://github.com/liamkf/Unreal_FASTBuild/issues/3 by Fire. Likewise following the steps here https://github.com/ClxS/FASTBuild-UE4 but using this version of the FASTBuild.cs file should also work.

There is also a few posts here http://knownshippable.com/blog/2017/03/07/fastbuild-with-unreal-engine-4-setup/ which may help people get setup with UE4, FASTBuild, as well as setting up distribution and caching.

## Notes for this fork
* FBuild.exe and LICENSE.txt are in "Engine/Binaries/ThirdParty/FASTBuild" directory and FBuildExePathOverride is set there. Then FBuild.exe doesn't need to be in PATH or installed on developer computer.
* Added some config variables BaseEngine.ini to control enable/disable of FASTBuild and FASTBuild shader compiler when something goes wrong (like bad worker, FASTBuild specific compilation problem or just testing) and can be changed in project version control (DefaultEngine.ini).
* In IsAvailable checks if FASTBUILD_BROKERAGE_PATH is set and doesn't use FASTBuild when it isn't.

## Setup FASTBuild shader compiler
Tested on Windows, Linux on windows, PS4 and XboxOne on UE 4.20
#### ShaderCompilerFastBuild.cpp
Copy ShaderCompilerFastBuild.cpp to `Engine/Source/Runtime/Engine/Private/ShaderCompiler/ShaderCompilerFastBuild.cpp`

#### ShaderCompiler.h
* Add `friend class FShaderCompileFASTBuildThreadRunnable;` next to `friend class FShaderCompileXGEThreadRunnable_XmlInterface;`
```
 #if PLATFORM_WINDOWS
 	friend class FShaderCompileXGEThreadRunnable_XmlInterface;
 	friend class FShaderCompileXGEThreadRunnable_InterceptionInterface;
+	friend class FShaderCompileFASTBuildThreadRunnable;
 #endif // PLATFORM_WINDOWS
```
* Add class `FShaderCompileFASTBuildThreadRunnable` under class `FShaderCompileXGEThreadRunnable_InterceptionInterface`.
Code is in ShaderCompiler.h in this repository.

#### ShaderCompilerXGE.cpp
* Few changes here to access functions from ShaderCompilerFastBuild.cpp
```
-static FString XGE_ConsolePath;
-static const FString XGE_ScriptFileName(TEXT("xgscript.xml"));
-static const FString XGE_InputFileName(TEXT("WorkerInput.in"));
-static const FString XGE_OutputFileName(TEXT("WorkerOutput.out"));
-static const FString XGE_SuccessFileName(TEXT("Success"));
+FString XGE_ConsolePath;
+FString XGE_ScriptFileName(TEXT("xgscript.xml"));
+const FString XGE_InputFileName(TEXT("WorkerInput.in"));
+const FString XGE_OutputFileName(TEXT("WorkerOutput.out"));
+const FString XGE_SuccessFileName(TEXT("Success"));

-static FArchive* CreateFileHelper(const FString& Filename)
+FArchive* CreateFileHelper(const FString& Filename)

-static void MoveFileHelper(const FString& To, const FString& From)
+void MoveFileHelper(const FString& To, const FString& From)

-static void DeleteFileHelper(const FString& Filename)
+void DeleteFileHelper(const FString& Filename)
```
#### ShaderCompiler.cpp
* Add FShaderCompileFASTBuildThreadRunnable as option before XGE shader compiler
```
-	if (FShaderCompileXGEThreadRunnable_InterceptionInterface::IsSupported() && bCanUseXGE)
+	if (FShaderCompileFASTBuildThreadRunnable::IsSupported() && bCanUseXGE)
 	{
+		UE_LOG(LogShaderCompilers, Display, TEXT("Using FASTBuild Shader Compiler."));
+		Thread = MakeUnique<FShaderCompileFASTBuildThreadRunnable>(this);
+		bIsUsingXGEInterface = true;
+	}
+	else if (FShaderCompileXGEThreadRunnable_InterceptionInterface::IsSupported() && bCanUseXGE)
```

## List of added/edited files as reference
* Engine/Binaries/ThirdParty/FASTBuild/FBuild.exe
* Engine/Binaries/ThirdParty/FASTBuild/LICENSE.TXT
* Engine/Build/InstalledEngineBuild.xml
* Engine/Build/InstalledEngineFilters.xml
* Engine/Config/BaseEngine.ini
* Engine/Source/Programs/UnrealBuildTool/Configuration/BuildConfiguration.cs
* Engine/Source/Programs/UnrealBuildTool/Configuration/UEBuildPlatform.cs
* Engine/Source/Programs/UnrealBuildTool/Platform/Linux/UEBuildLinux.cs
* Engine/Source/Programs/UnrealBuildTool/Platform/PS4/UEBuildPS4.cs
* Engine/Source/Programs/UnrealBuildTool/Platform/Windows/UEBuildWindows.cs
* Engine/Source/Programs/UnrealBuildTool/Platform/XboxOne/UEBuildXboxOne.cs
* Engine/Source/Programs/UnrealBuildTool/System/ActionGraph.cs
* Engine/Source/Programs/UnrealBuildTool/System/FASTBuild.cs
* Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.cs
* Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.csproj
* Engine/Source/Runtime/Engine/Private/ShaderCompiler/ShaderCompiler.cpp
* Engine/Source/Runtime/Engine/Private/ShaderCompiler/ShaderCompilerFastBuild.cpp
* Engine/Source/Runtime/Engine/Private/ShaderCompiler/ShaderCompilerXGE.cpp
* Engine/Source/Runtime/Engine/Public/ShaderCompiler.h
