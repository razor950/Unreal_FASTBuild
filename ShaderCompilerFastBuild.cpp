#include "ShaderCompiler.h"
#include "HAL/PlatformFilemanager.h"
#include "GenericPlatform/GenericPlatformFile.h"
#include "HAL/FileManager.h"
#include "Misc/ScopeLock.h"
#include "Internationalization/Regex.h"

#if PLATFORM_WINDOWS // FASTBuild shader compilation is only supported on Windows.

// --------------------------------------------------------------------------
//                          Legacy XGE Xml interface                         
// --------------------------------------------------------------------------

extern FArchive* CreateFileHelper(const FString& Filename);
extern void DeleteFileHelper(const FString& Filename);
extern void MoveFileHelper(const FString& To, const FString& From);

// --------------------------------------------------------------------------
//                          FastBuild interface                              
// --------------------------------------------------------------------------
namespace FASTBuildConsoleVariables
{
	/** The total number of batches to fill with shaders before creating another group of batches. */
	int32 BatchGroupSize = 128;
	FAutoConsoleVariableRef CVarFASTBuildShaderCompileBatchGroupSize(
		TEXT("r.FASTBuildShaderCompile.BatchGroupSize"),
		BatchGroupSize,
		TEXT("Specifies the number of batches to fill with shaders.\n")
		TEXT("Shaders are spread across this number of batches until all the batches are full.\n")
		TEXT("This allows the FASTBuild compile to go wider when compiling a small number of shaders.\n")
		TEXT("Default = 128\n"),
		ECVF_Default);

	int32 Enabled = 1;
	FAutoConsoleVariableRef CVarFASTBuildShaderCompile(
		TEXT("r.FASTBuildShaderCompile"),
		Enabled,
		TEXT("Enables or disables the use of FASTBuild to build shaders.\n")
		TEXT("0: Local builds only. \n")
		TEXT("1: Distribute builds using FASTBuild."),
		ECVF_Default);

	/** The maximum number of shaders to group into a single FASTBuild task. */
	int32 BatchSize = 16;
	FAutoConsoleVariableRef CVarFASTBuildShaderCompileBatchSize(
		TEXT("r.FASTBuildShaderCompile.BatchSize"),
		BatchSize,
		TEXT("Specifies the number of shaders to batch together into a single FASTBuild task.\n")
		TEXT("Default = 16\n"),
		ECVF_Default);

	/**
	* The number of seconds to wait after a job is submitted before kicking off the XGE process.
	* This allows time for the engine to enqueue more shaders, so we get better batching.
	*/
	float JobTimeout = 0.5f;
	FAutoConsoleVariableRef CVarXGEShaderCompileJobTimeout(
		TEXT("r.FASTBuildShaderCompile.JobTimeout"),
		JobTimeout,
		TEXT("The number of seconds to wait for additional shader jobs to be submitted before starting a build.\n")
		TEXT("Default = 0.5\n"),
		ECVF_Default);
}

static FString GetFASTBuild_ExecutablePath()
{
	return FPaths::ConvertRelativePathToFull(FPaths::EngineDir()) / TEXT("Extras\\FASTBuild\\FBuild.exe");
}

static const FString FASTBuild_CachePath(TEXT("..\\Saved\\FASTBuildCache"));
static const FString FASTBuild_OrbisSDKToolchainRoot(TEXT("Engine\\Binaries\\ThirdParty\\PS4\\OrbisSDK"));
static const FString FASTBuild_DurangoXDKToolchainRoot(TEXT("Engine\\Binaries\\ThirdParty\\XboxOne\\DurangoXDK"));
static const FString FASTBuild_Toolchain[]
{
	TEXT("Engine\\Binaries\\ThirdParty\\Windows\\DirectX\\x64\\d3dcompiler_47.dll")
};
static const FString FASTBuild_OrbisSDKToolchain[]
{
	TEXT("host_tools\\bin\\at9tool.exe"),
	TEXT("host_tools\\bin\\libSceGnm.dll"),
	TEXT("host_tools\\bin\\libSceGnm_debug.dll"),
	TEXT("host_tools\\bin\\libSceGnmx.dll"),
	TEXT("host_tools\\bin\\libSceGnmx_debug.dll"),
	TEXT("host_tools\\bin\\libSceGpuAddress.dll"),
	TEXT("host_tools\\bin\\libSceGpuAddress_debug.dll"),
	TEXT("host_tools\\bin\\libSceShaderBinary.dll"),
	TEXT("host_tools\\bin\\libSceShaderPerf.dll"),
	TEXT("host_tools\\bin\\libSceShaderWavePsslc.dll"),
	TEXT("host_tools\\bin\\orbis-bin.exe"),
	TEXT("host_tools\\bin\\orbis-clang.exe"),
	TEXT("host_tools\\bin\\orbis-clang.exe.sn-dbs-tool.ini"),
	TEXT("host_tools\\bin\\orbis-cu-as.exe"),
	TEXT("host_tools\\bin\\orbis-sb-dump.exe"),
	TEXT("host_tools\\bin\\orbis-snarl.exe"),
	TEXT("host_tools\\bin\\orbis-symupload.exe"),
	TEXT("host_tools\\bin\\orbis-wave-psslc.exe"),
	TEXT("host_tools\\bin\\orbis-wave-psslc.exe.sn-dbs-tool.ini")
};
static const FString FASTBuild_DurangoXDKToolchain[]
{
	TEXT("bin\\xg.dll"),
	TEXT("bin\\XtfApplication.dll"),
	TEXT("bin\\XtfConsoleControl.dll"),
	TEXT("bin\\XtfConsoleManager.dll"),
	TEXT("bin\\XtfDebugMonitor.dll"),
	TEXT("bin\\XtfFileIO.dll")
};
static const FString FASTBuild_DurangoXDKVersionToolchain[]
{
	TEXT("bin\\d3d12_x.dll"),
	TEXT("bin\\PhSO.exe"),
	TEXT("bin\\PhSO_pc.dll"),
	TEXT("bin\\umd12_pc.dll"),
	TEXT("bin\\xg.dll"),
	TEXT("xdk\\FXC\\amd64\\D3DCompiler_47_xdk.dll"),
	TEXT("xdk\\FXC\\amd64\\FXC.exe"),
	TEXT("xdk\\FXC\\amd64\\SC_DLL.dll")
};

static const FString FASTBuild_ScriptFileName(TEXT("fastbuildscript.bff"));
static const FString FASTBuild_InputFileName(TEXT("Worker.in"));
static const FString FASTBuild_OutputFileName(TEXT("Worker.out"));
static const FString FASTBuild_SuccessFileName(TEXT("Success"));

bool FShaderCompileFASTBuildThreadRunnable::IsSupported()
{
	if (FASTBuildConsoleVariables::Enabled == 1)
	{
		// Check to see if the FASTBuild exe exists.
		IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();

		if (!PlatformFile.FileExists(*GetFASTBuild_ExecutablePath()))
		{
			UE_LOG(LogShaderCompilers, Warning, TEXT("Cannot use FASTBuild Shader Compiler as FASTBuild is not found. Target Path:%s"), *GetFASTBuild_ExecutablePath());
			FASTBuildConsoleVariables::Enabled = 0;
		}

		// Check that fast build brokerage path is set. No point to use fast build for local only.
		FString FBuildBrokerage = FPlatformMisc::GetEnvironmentVariable(TEXT("FASTBUILD_BROKERAGE_PATH"));
		if (FBuildBrokerage.IsEmpty())
		{
			UE_LOG(LogShaderCompilers, Log, TEXT("Won't use FASTBuild Shader Compiler as FASTBUILD_BROKERAGE_PATH is not set."));
			FASTBuildConsoleVariables::Enabled = 0;
		}
	}

	return FASTBuildConsoleVariables::Enabled == 1;
}


/** Initialization constructor. */
FShaderCompileFASTBuildThreadRunnable::FShaderCompileFASTBuildThreadRunnable(class FShaderCompilingManager* InManager)
	: FShaderCompileThreadRunnableBase(InManager)
	, BuildProcessID(INDEX_NONE)
	, ShaderBatchesInFlightCompleted(0)
	, FASTBuildWorkingDirectory(InManager->AbsoluteShaderBaseWorkingDirectory / TEXT("FASTBuild"))
	, FASTBuildDirectoryIndex(0)
	, LastAddTime(0)
	, StartTime(0)
	, BatchIndexToCreate(0)
	, BatchIndexToFill(0)
{
}

FShaderCompileFASTBuildThreadRunnable::~FShaderCompileFASTBuildThreadRunnable()
{
	if (BuildProcessHandle.IsValid())
	{
		// We still have a build in progress.
		// Kill it...
		FPlatformProcess::TerminateProc(BuildProcessHandle);
		FPlatformProcess::CloseProc(BuildProcessHandle);
	}

	// Clean up any intermediate files/directories we've got left over.
	IFileManager::Get().DeleteDirectory(*FASTBuildWorkingDirectory, false, true);

	// Delete all the shader batch instances we have.
	for (FShaderBatch* Batch : ShaderBatchesIncomplete)
		delete Batch;

	for (FShaderBatch* Batch : ShaderBatchesInFlight)
		delete Batch;

	for (FShaderBatch* Batch : ShaderBatchesFull)
		delete Batch;

	ShaderBatchesIncomplete.Empty();
	ShaderBatchesInFlight.Empty();
	ShaderBatchesFull.Empty();
}


void FShaderCompileFASTBuildThreadRunnable::PostCompletedJobsForBatch(FShaderBatch* Batch)
{
	// Enter the critical section so we can access the input and output queues
	FScopeLock Lock(&Manager->CompileQueueSection);
	for (FShaderCommonCompileJob* Job : Batch->GetJobs())
	{
		FShaderMapCompileResults& ShaderMapResults = Manager->ShaderMapJobs.FindChecked(Job->Id);
		ShaderMapResults.FinishedJobs.Add(Job);
		ShaderMapResults.bAllJobsSucceeded = ShaderMapResults.bAllJobsSucceeded && Job->bSucceeded;
	}

	// Using atomics to update NumOutstandingJobs since it is read outside of the critical section
	FPlatformAtomics::InterlockedAdd(&Manager->NumOutstandingJobs, -Batch->NumJobs());
}

void FShaderCompileFASTBuildThreadRunnable::FShaderBatch::AddJob(FShaderCommonCompileJob* Job)
{
	// We can only add jobs to a batch which hasn't been written out yet.
	if (bTransferFileWritten)
	{
		UE_LOG(LogShaderCompilers, Fatal, TEXT("Attempt to add shader compile jobs to a FASTBuild shader batch which has already been written to disk."));
	}
	else
	{
		Jobs.Add(Job);
	}
}

void FShaderCompileFASTBuildThreadRunnable::FShaderBatch::WriteTransferFile()
{
	// Write out the file that the worker app is waiting for, which has all the information needed to compile the shader.
	FArchive* TransferFile = CreateFileHelper(InputFileNameAndPath);
	FShaderCompileUtilities::DoWriteTasks(Jobs, *TransferFile);
	delete TransferFile;

	bTransferFileWritten = true;
}

void FShaderCompileFASTBuildThreadRunnable::FShaderBatch::SetIndices(int32 InDirectoryIndex, int32 InBatchIndex)
{
	DirectoryIndex = InDirectoryIndex;
	BatchIndex = InBatchIndex;

	WorkingDirectory = FString::Printf(TEXT("%s/%d/%d"), *DirectoryBase, DirectoryIndex, BatchIndex);

	InputFileNameAndPath = WorkingDirectory / InputFileName;
	OutputFileNameAndPath = WorkingDirectory / OutputFileName;
	SuccessFileNameAndPath = WorkingDirectory / SuccessFileName;
}

void FShaderCompileFASTBuildThreadRunnable::FShaderBatch::CleanUpFiles(bool keepInputFile)
{
	if (!keepInputFile)
	{
		DeleteFileHelper(InputFileNameAndPath);
	}

	DeleteFileHelper(OutputFileNameAndPath);
	DeleteFileHelper(SuccessFileNameAndPath);
}

static void FASTBuildAddToScriptFile(FArchive* InScriptFile, const FString &InExtraFile)
{
	InScriptFile->Serialize((void*)StringCast<ANSICHAR>(*InExtraFile, InExtraFile.Len()).Get(), sizeof(ANSICHAR) * InExtraFile.Len());
}

static void FASTBuildCopyFileToDest(const FString &SourceDir, const FString &DestDir, const FString &ExtraFilePartialPath)
{
	FString ExtraFilePath = *(SourceDir / ExtraFilePartialPath);
	FString ExtraFileDestPath = *(DestDir / ExtraFilePartialPath);
	FPaths::NormalizeDirectoryName(ExtraFilePath);
	FPaths::NormalizeDirectoryName(ExtraFileDestPath);

	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	PlatformFile.CreateDirectoryTree(*FPaths::GetPath(ExtraFileDestPath));
	if (PlatformFile.FileExists(*ExtraFileDestPath))
		PlatformFile.DeleteFile(*ExtraFileDestPath);
	PlatformFile.CopyFile(*ExtraFileDestPath, *ExtraFilePath);
}

static void WriteFASTBuildScriptFileHeader(FArchive* ScriptFile, const FString& WorkerName)
{
	static const TCHAR HeaderTemplate[] =
		TEXT("Settings\r\n")
		TEXT("{\r\n")
		TEXT("\t.CachePath = '%s'\r\n")
		TEXT("}\r\n")
		TEXT("\r\n")
		TEXT("Compiler('ShaderCompiler')\r\n")
		TEXT("{\r\n")
		TEXT("\t.CompilerFamily = 'custom'\r\n")
		TEXT("\t.Executable = '%s'\r\n")
		TEXT("\t.ExecutableRootPath = '%s'\r\n")
		TEXT("\t.SimpleDistributionMode = true\r\n")
		TEXT("\t.CustomEnvironmentVariables = \r\n")
		TEXT("\t{\r\n")
		TEXT("\t\t'SCE_ORBIS_SDK_DIR=%%1%s',\r\n")
		TEXT("\t\t'DurangoXDK=%%1%s\\'\r\n")
		TEXT("\t}\r\n")
		TEXT("\t.ExtraFiles = \r\n")
		TEXT("\t{\r\n");

	FString HeaderString = FString::Printf(HeaderTemplate, *FASTBuild_CachePath, *WorkerName,
																				 *IFileManager::Get().ConvertToAbsolutePathForExternalAppForRead(*FPaths::RootDir()),
																				 *FASTBuild_OrbisSDKToolchainRoot, *FASTBuild_DurangoXDKToolchainRoot);
	FASTBuildAddToScriptFile(ScriptFile, HeaderString);

	for (const FString& ExtraFilePartialPath : FASTBuild_Toolchain)
	{
		FString ExtraFile = TEXT("\t\t'") + IFileManager::Get().ConvertToAbsolutePathForExternalAppForRead(*(FPaths::RootDir() / ExtraFilePartialPath)) + TEXT("',\r\n");
		FASTBuildAddToScriptFile(ScriptFile, ExtraFile);
	}

	class FDependencyEnumerator : public IPlatformFile::FDirectoryVisitor
	{
	public:
		FDependencyEnumerator(FArchive* InScriptFile, const TCHAR* InPrefix, const TCHAR* InExtension)
			: ScriptFile(InScriptFile)
			, Prefix(InPrefix)
			, Extension(InExtension)
		{
		}

		virtual bool Visit(const TCHAR* FilenameChar, bool bIsDirectory) override
		{
			if (!bIsDirectory)
			{
				FString Filename = FString(FilenameChar);

				if ((!Prefix || Filename.Contains(Prefix)) && (!Extension || Filename.EndsWith(Extension)))
				{
					FString ExtraFile = TEXT("\t\t'") + IFileManager::Get().ConvertToAbsolutePathForExternalAppForWrite(*Filename) + TEXT("',\r\n");
					FASTBuildAddToScriptFile(ScriptFile, ExtraFile);
				}
			}

			return true;
		}

		FArchive *const ScriptFile;
		const TCHAR* Prefix;
		const TCHAR* Extension;
	};

	class FRegexVisitor: public IPlatformFile::FDirectoryVisitor
	{
	public:
		FRegexVisitor(TArray<FString> &InFileNames, const FRegexPattern& InRegexPattern)
			: FileNames(InFileNames)
			, RegexPattern(InRegexPattern)
		{
		}

		virtual bool Visit(const TCHAR* FilenameChar, bool bIsDirectory) override
		{
			FString Filename = FString(FilenameChar);
			FRegexMatcher FileNameRegexMatcher(RegexPattern, Filename);

			if (FileNameRegexMatcher.FindNext())
			{
				FileNames.Add(Filename);
			}

			return true;
		}

		TArray<FString> &FileNames;
		const FRegexPattern& RegexPattern;
	};

	// Get path to PS4 SDK.
	FString PS4SDKDir = FPlatformMisc::GetEnvironmentVariable(TEXT("SCE_ORBIS_SDK_DIR"));

	// If SDK found prepare files.
	if (!PS4SDKDir.IsEmpty())
	{
		// Copy SDK files and copy them to local directory.
		FString PS4ToolchainRoot = *(FPaths::RootDir() / FASTBuild_OrbisSDKToolchainRoot);

		for (const FString& ExtraFilePartialPath : FASTBuild_OrbisSDKToolchain)
		{
			FASTBuildCopyFileToDest(PS4SDKDir, PS4ToolchainRoot, ExtraFilePartialPath);
		}

		// Add all files in copied folder to ExtraFiles.
		FDependencyEnumerator PS4Deps = FDependencyEnumerator(ScriptFile, nullptr, nullptr);
		IFileManager::Get().IterateDirectoryRecursively(*PS4ToolchainRoot, PS4Deps);
	}

	// Get path to XBox SDK.
	FString XboxOneDir = FPlatformMisc::GetEnvironmentVariable(TEXT("DurangoXDK"));

	if (!XboxOneDir.IsEmpty())
	{
		// Copy SDK files and copy them to local directory.
		FString XboxToolchainRoot = *(FPaths::RootDir() / FASTBuild_DurangoXDKToolchainRoot);

		for (const FString& ExtraFilePartialPath : FASTBuild_DurangoXDKToolchain)
		{
			FASTBuildCopyFileToDest(XboxOneDir, XboxToolchainRoot, ExtraFilePartialPath);
		}

		// Find all directories with 6 digits as XDK versions.
		TArray<FString> FolderNames;
		FRegexPattern XDKVersionPattern(TEXT("^.*(\\d{6})$"));
		FRegexVisitor Visitor = FRegexVisitor(FolderNames, XDKVersionPattern);
		IFileManager::Get().IterateDirectory(*XboxOneDir, Visitor);
		for (int i = 0; i < FolderNames.Num(); ++i)
		{
			const FString &XDKVersion = FPaths::GetCleanFilename(FolderNames[i]);
			// Copy SDK bin files and copy them to local directory.
			FString XboxToolchainVersionRoot = *(XboxToolchainRoot / *XDKVersion);
			FString XboxOneVersionDirStr = *(XboxOneDir / *XDKVersion);

			for (const FString& ExtraFilePartialPath : FASTBuild_DurangoXDKVersionToolchain)
			{
				FASTBuildCopyFileToDest(XboxOneVersionDirStr, XboxToolchainVersionRoot, ExtraFilePartialPath);
			}
		}

		// Add all files in copied folder to ExtraFiles.
		FDependencyEnumerator XboxDeps = FDependencyEnumerator(ScriptFile, nullptr, nullptr);
		IFileManager::Get().IterateDirectoryRecursively(*XboxToolchainRoot, XboxDeps);
	}

	// Add all files in modules directory starting with ShaderCompileWorker.
	FDependencyEnumerator DllDeps = FDependencyEnumerator(ScriptFile, TEXT("ShaderCompileWorker-"), TEXT(".dll"));
	IFileManager::Get().IterateDirectoryRecursively(*FPlatformProcess::GetModulesDirectory(), DllDeps);
	// Uncomment when you need to get call stack of shader compiler on remote machine.
// 	FDependencyEnumerator DllDepsPdb = FDependencyEnumerator(ScriptFile, TEXT("ShaderCompileWorker"), TEXT(".pdb"));
// 	IFileManager::Get().IterateDirectoryRecursively(*FPlatformProcess::GetModulesDirectory(), DllDepsPdb);

	FDependencyEnumerator ModulesDeps = FDependencyEnumerator(ScriptFile, TEXT("ShaderCompileWorker"), TEXT(".modules"));
	IFileManager::Get().IterateDirectoryRecursively(*FPlatformProcess::GetModulesDirectory(), ModulesDeps);

	// Add all shader files found in Plugins and Shader directories.
	FDependencyEnumerator ShaderUshDeps = FDependencyEnumerator(ScriptFile, nullptr, TEXT(".ush"));
	IFileManager::Get().IterateDirectoryRecursively(FPlatformProcess::ShaderDir(), ShaderUshDeps);

	FDependencyEnumerator ShaderUsfDeps = FDependencyEnumerator(ScriptFile, nullptr, TEXT(".usf"));
	IFileManager::Get().IterateDirectoryRecursively(FPlatformProcess::ShaderDir(), ShaderUsfDeps);

	FDependencyEnumerator PluginShaderUshDeps = FDependencyEnumerator(ScriptFile, nullptr, TEXT(".ush"));
	IFileManager::Get().IterateDirectoryRecursively(*FPaths::Combine(*(FPaths::EngineDir()), TEXT("Plugins")), PluginShaderUshDeps);

	FDependencyEnumerator PluginShaderUsfDeps = FDependencyEnumerator(ScriptFile, nullptr, TEXT(".usf"));
	IFileManager::Get().IterateDirectoryRecursively(*FPaths::Combine(*(FPaths::EngineDir()), TEXT("Plugins")), PluginShaderUsfDeps);

	const FString ExtraFilesFooter =
		TEXT("\t}\r\n")
		TEXT("}\r\n");
	FASTBuildAddToScriptFile(ScriptFile, ExtraFilesFooter);

}

static void WriteFASTBuildScriptFileFooter(FArchive* ScriptFile)
{
}

void FShaderCompileFASTBuildThreadRunnable::GatherResultsFromFASTBuild()
{
	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	IFileManager& FileManager = IFileManager::Get();

	// Reverse iterate so we can remove batches that have completed as we go.
	for (int32 Index = ShaderBatchesInFlight.Num() - 1; Index >= 0; Index--)
	{
		FShaderBatch* Batch = ShaderBatchesInFlight[Index];

		// If this batch is completed already, skip checks.
		if (Batch->bSuccessfullyCompleted)
		{
			continue;
		}

		// Instead of checking for another file, we just check to see if the file exists, and if it does, we check if it has a write lock on it. FASTBuild never does partial writes, so this should tell us if FASTBuild is complete.
		// Perform the same checks on the worker output file to verify it came from this build.
		if (PlatformFile.FileExists(*Batch->OutputFileNameAndPath))
		{
			if (FileManager.FileSize(*Batch->OutputFileNameAndPath) > 0)
			{
				IFileHandle* Handle = PlatformFile.OpenWrite(*Batch->OutputFileNameAndPath, true);
				if (Handle)
				{
					delete Handle;

					if (PlatformFile.GetTimeStamp(*Batch->OutputFileNameAndPath) >= ScriptFileCreationTime)
					{
						FArchive* OutputFilePtr = FileManager.CreateFileReader(*Batch->OutputFileNameAndPath, FILEREAD_Silent);
						if (OutputFilePtr)
						{
							FArchive& OutputFile = *OutputFilePtr;
							FShaderCompileUtilities::DoReadTaskResults(Batch->GetJobs(), OutputFile);

							// Close the output file.
							delete OutputFilePtr;

							// Cleanup the worker files
							// Do NOT clean up files until the whole batch is done, so we can clean them all up once the fastbuild process exits. Otherwise there is a race condition between FastBuild checking the output files, and us deleting them here.
							//Batch->CleanUpFiles(false);			// (false = don't keep the input file)
							Batch->bSuccessfullyCompleted = true;
							PostCompletedJobsForBatch(Batch);
							//ShaderBatchesInFlight.RemoveAt(Index);
							ShaderBatchesInFlightCompleted++;
							//delete Batch;
						}
					}
				}
			}
		}
	}
}

int32 FShaderCompileFASTBuildThreadRunnable::CompilingLoop()
{
	bool bWorkRemaining = false;

	// We can only run one XGE build at a time.
	// Check if a build is currently in progress.
	if (BuildProcessHandle.IsValid())
	{
		// Read back results from the current batches in progress.
		GatherResultsFromFASTBuild();

		bool bDoExitCheck = false;
		if (FPlatformProcess::IsProcRunning(BuildProcessHandle))
		{
			if (ShaderBatchesInFlight.Num() == ShaderBatchesInFlightCompleted)
			{
				// We've processed all batches.
				// Wait for the XGE console process to exit
				FPlatformProcess::WaitForProc(BuildProcessHandle);
				bDoExitCheck = true;
			}
		}
		else
		{
			bDoExitCheck = true;
		}

		if (bDoExitCheck)
		{
			if (ShaderBatchesInFlight.Num() > ShaderBatchesInFlightCompleted)
			{
				// The build process has stopped.
				// Do one final pass over the output files to gather any remaining results.
				GatherResultsFromFASTBuild();
			}

			// The build process is no longer running.
			// We need to check the return code for possible failure
			int32 ReturnCode = 0;
			FPlatformProcess::GetProcReturnCode(BuildProcessHandle, &ReturnCode);

			switch (ReturnCode)
			{
			case 0:
				// No error
				break;

			case 1:
				// One or more of the shader compile worker processes crashed.
				UE_LOG(LogShaderCompilers, Fatal, TEXT("An error occurred during an XGE shader compilation job. One or more of the shader compile worker processes exited unexpectedly (Code 1)."));
				break;

			case 2:
				// Fatal IncrediBuild error
				UE_LOG(LogShaderCompilers, Fatal, TEXT("An error occurred during an FASTBuild shader compilation job. XGConsole.exe returned a fatal Incredibuild error (Code 2)."));
				break;

			case 3:
				// User canceled the build
				UE_LOG(LogShaderCompilers, Display, TEXT("The user terminated an XGE shader compilation job. Incomplete shader jobs will be redispatched in another FASTBuild build."));
				break;

			default:
				UE_LOG(LogShaderCompilers, Display, TEXT("An unknown error occurred during an XGE shader compilation job (Code %d). Incomplete shader jobs will be redispatched in another FASTBuild build."), ReturnCode);
				break;
			}


			// Reclaim jobs from the workers which did not succeed (if any).
			for (int i = 0; i < ShaderBatchesInFlight.Num(); ++i)
			{
				FShaderBatch* Batch = ShaderBatchesInFlight[i];

				if (Batch->bSuccessfullyCompleted)
				{
					// If we completed successfully, clean up.
					//PostCompletedJobsForBatch(Batch);
					Batch->CleanUpFiles(false);

					// This will be a dangling pointer until we clear the array at the end of this for loop
					delete Batch;
				}
				else
				{

					// Delete any output/success files, but keep the input file so we don't have to write it out again.
					Batch->CleanUpFiles(true);

					// We can't add any jobs to a shader batch which has already been written out to disk,
					// so put the batch back into the full batches list, even if the batch isn't full.
					ShaderBatchesFull.Add(Batch);

					// Reset the batch/directory indices and move the input file to the correct place.
					FString OldInputFilename = Batch->InputFileNameAndPath;
					Batch->SetIndices(FASTBuildDirectoryIndex, BatchIndexToCreate++);
					MoveFileHelper(Batch->InputFileNameAndPath, OldInputFilename);
				}
			}
			ShaderBatchesInFlightCompleted = 0;
			ShaderBatchesInFlight.Empty();
			FPlatformProcess::CloseProc(BuildProcessHandle);
		}

		bWorkRemaining |= ShaderBatchesInFlight.Num() > ShaderBatchesInFlightCompleted;
	}
	// No build process running. Check if we can kick one off now.
	else
	{
		// Determine if enough time has passed to allow a build to kick off.
		// Since shader jobs are added to the shader compile manager asynchronously by the engine, 
		// we want to give the engine enough time to queue up a large number of shaders.
		// Otherwise we will only be kicking off a small number of shader jobs at once.
		bool BuildDelayElapsed = (((FPlatformTime::Cycles() - LastAddTime) * FPlatformTime::GetSecondsPerCycle()) >= FASTBuildConsoleVariables::JobTimeout);
		bool HasJobsToRun = (ShaderBatchesIncomplete.Num() > 0 || ShaderBatchesFull.Num() > 0);

		if (BuildDelayElapsed && HasJobsToRun && ShaderBatchesInFlight.Num() == ShaderBatchesInFlightCompleted)
		{
			// Move all the pending shader batches into the in-flight list.
			ShaderBatchesInFlight.Reserve(ShaderBatchesIncomplete.Num() + ShaderBatchesFull.Num());

			for (FShaderBatch* Batch : ShaderBatchesIncomplete)
			{
				// Check we've actually got jobs for this batch.
				check(Batch->NumJobs() > 0);

				// Make sure we've written out the worker files for any incomplete batches.
				Batch->WriteTransferFile();
				ShaderBatchesInFlight.Add(Batch);
			}

			for (FShaderBatch* Batch : ShaderBatchesFull)
			{
				// Check we've actually got jobs for this batch.
				check(Batch->NumJobs() > 0);

				ShaderBatchesInFlight.Add(Batch);
			}

			ShaderBatchesFull.Empty();
			ShaderBatchesIncomplete.Empty(FASTBuildConsoleVariables::BatchGroupSize);

			FString ScriptFilename = FASTBuildWorkingDirectory / FString::FromInt(FASTBuildDirectoryIndex) / FASTBuild_ScriptFileName;
			UE_LOG(LogShaderCompilers, Log, TEXT("Script Path:%s"), *ScriptFilename);
			// Create the FASTBuild script file.
			FArchive* ScriptFile = CreateFileHelper(ScriptFilename);
			WriteFASTBuildScriptFileHeader(ScriptFile, Manager->ShaderCompileWorkerName);

			// Write the XML task line for each shader batch
			for (FShaderBatch* Batch : ShaderBatchesInFlight)
			{
				FString WorkerAbsoluteDirectory = IFileManager::Get().ConvertToAbsolutePathForExternalAppForWrite(*Batch->WorkingDirectory);
				FPaths::NormalizeDirectoryName(WorkerAbsoluteDirectory);

				// Proper path should be "WorkingDir" 0 0 "Worker.in" "Worker.out"

				FString ExecFunction = FString::Printf(
					TEXT("ObjectList('ShaderBatch-%d')\r\n")
					TEXT("{\r\n")
					TEXT("\t.Compiler = 'ShaderCompiler'\r\n")
					TEXT("\t.CompilerOptions = '\"\" %d %d \"%%1\" \"%%2\"'\r\n")
					TEXT("\t.CompilerOutputExtension = '.out'\r\n")
					TEXT("\t.CompilerInputFiles = { '%s' }\r\n")
					TEXT("\t.CompilerOutputPath = '%s'\r\n")
					TEXT("}\r\n\r\n"),
					Batch->BatchIndex,
					Manager->ProcessId,
					Batch->BatchIndex,
					*Batch->InputFileNameAndPath,
					*WorkerAbsoluteDirectory);

				//FString TaskXML = FString::Printf(TEXT("\t\t\t<Task Caption=\"Compiling %d Shaders (Batch %d)\" Params=\"%s\" />\r\n"), Batch->NumJobs(), Batch->BatchIndex, *WorkerParameters);

				ScriptFile->Serialize((void*)StringCast<ANSICHAR>(*ExecFunction, ExecFunction.Len()).Get(), sizeof(ANSICHAR) * ExecFunction.Len());
			}


			FString AliasBuildTargetOpen = FString(
				TEXT("Alias('all')\r\n")
				TEXT("{\r\n")
				TEXT("\t.Targets = { \r\n")
			);
			ScriptFile->Serialize((void*)StringCast<ANSICHAR>(*AliasBuildTargetOpen, AliasBuildTargetOpen.Len()).Get(), sizeof(ANSICHAR) * AliasBuildTargetOpen.Len());

			// Write write the "All" target
			for (FShaderBatch* Batch : ShaderBatchesInFlight)
			{
				FString TargetExport = FString::Printf(TEXT("'ShaderBatch-%d', "), Batch->BatchIndex);

				ScriptFile->Serialize((void*)StringCast<ANSICHAR>(*TargetExport, TargetExport.Len()).Get(), sizeof(ANSICHAR) * TargetExport.Len());
			}

			FString AliasBuildTargetClose = FString(TEXT(" }\r\n}\r\n"));
			ScriptFile->Serialize((void*)StringCast<ANSICHAR>(*AliasBuildTargetClose, AliasBuildTargetClose.Len()).Get(), sizeof(ANSICHAR) * AliasBuildTargetClose.Len());

			// End the XML script file and close it.
			WriteFASTBuildScriptFileFooter(ScriptFile);
			delete ScriptFile;
			ScriptFile = nullptr;

			// Grab the timestamp from the script file.
			// We use this to ignore any left over files from previous builds by only accepting files created after the script file.
			ScriptFileCreationTime = IFileManager::Get().GetTimeStamp(*ScriptFilename);

			StartTime = FPlatformTime::Cycles();

			// Use stop on errors so we can respond to shader compile worker crashes immediately.
			// Regular shader compilation errors are not returned as worker errors.
			FString FBConsoleArgs = TEXT("-config \"") + ScriptFilename + TEXT("\" -dist -monitor -cache -ide");

			// Kick off the FASTBuild process...
			BuildProcessHandle = FPlatformProcess::CreateProc(*GetFASTBuild_ExecutablePath(), *FBConsoleArgs, false, false, true, &BuildProcessID, 0, nullptr, nullptr);
			if (!BuildProcessHandle.IsValid())
			{
				UE_LOG(LogShaderCompilers, Fatal, TEXT("Failed to launch %s during shader compilation."), *GetFASTBuild_ExecutablePath());
			}

			// If the engine crashes, we don't get a chance to kill the build process.
			// Start up the build monitor process to monitor for engine crashes.
			uint32 BuildMonitorProcessID;
			FProcHandle BuildMonitorHandle = FPlatformProcess::CreateProc(*Manager->ShaderCompileWorkerName, *FString::Printf(TEXT("-xgemonitor %d %d"), Manager->ProcessId, BuildProcessID), true, false, false, &BuildMonitorProcessID, 0, nullptr, nullptr);
			FPlatformProcess::CloseProc(BuildMonitorHandle);

			// Reset batch counters and switch directories
			BatchIndexToFill = 0;
			BatchIndexToCreate = 0;
			FASTBuildDirectoryIndex = 1 - FASTBuildDirectoryIndex;

			bWorkRemaining = true;
		}
	}

	// Try to prepare more shader jobs (even if a build is in flight).
	TArray<FShaderCommonCompileJob*> JobQueue;
	{
		// Enter the critical section so we can access the input and output queues
		FScopeLock Lock(&Manager->CompileQueueSection);

		// Grab as many jobs from the job queue as we can.
		int32 NumNewJobs = Manager->CompileQueue.Num();
		if (NumNewJobs > 0)
		{
			int32 DestJobIndex = JobQueue.AddUninitialized(NumNewJobs);
			for (int32 SrcJobIndex = 0; SrcJobIndex < NumNewJobs; SrcJobIndex++, DestJobIndex++)
			{
				JobQueue[DestJobIndex] = Manager->CompileQueue[SrcJobIndex];
			}

			Manager->CompileQueue.RemoveAt(0, NumNewJobs);
		}
	}

	if (JobQueue.Num() > 0)
	{
		// We have new jobs in the queue.
		// Group the jobs into batches and create the worker input files.
		for (int32 JobIndex = 0; JobIndex < JobQueue.Num(); JobIndex++)
		{
			if (BatchIndexToFill >= ShaderBatchesIncomplete.GetMaxIndex() || !ShaderBatchesIncomplete.IsAllocated(BatchIndexToFill))
			{
				// There are no more incomplete shader batches available.
				// Create another one...
				ShaderBatchesIncomplete.Insert(BatchIndexToFill, new FShaderBatch(
					FASTBuildWorkingDirectory,
					FASTBuild_InputFileName,
					FASTBuild_SuccessFileName,
					FASTBuild_OutputFileName,
					FASTBuildDirectoryIndex,
					BatchIndexToCreate));

				BatchIndexToCreate++;
			}

			// Add a single job to this batch
			FShaderBatch* CurrentBatch = ShaderBatchesIncomplete[BatchIndexToFill];
			CurrentBatch->AddJob(JobQueue[JobIndex]);

			// If the batch is now full...
			if (CurrentBatch->NumJobs() == FASTBuildConsoleVariables::BatchSize)
			{
				CurrentBatch->WriteTransferFile();

				// Move the batch to the full list.
				ShaderBatchesFull.Add(CurrentBatch);
				ShaderBatchesIncomplete.RemoveAt(BatchIndexToFill);
			}

			BatchIndexToFill++;
			BatchIndexToFill %= FASTBuildConsoleVariables::BatchGroupSize;
		}

		// Keep track of the last time we added jobs.
		LastAddTime = FPlatformTime::Cycles();

		bWorkRemaining = true;
	}

	if (Manager->bAllowAsynchronousShaderCompiling)
	{
		// Yield for a short while to stop this thread continuously polling the disk.
		FPlatformProcess::Sleep(0.01f);
	}

	return bWorkRemaining ? 1 : 0;
}

#endif //PLATFORM_WINDOWS
