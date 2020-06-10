class FShaderCompileFASTBuildThreadRunnable : public FShaderCompileThreadRunnableBase
{
private:
	/** The handle referring to the FASTBuild console process, if a build is in progress. */
	FProcHandle BuildProcessHandle;

	/** Process ID of the FASTBuild console, if a build is in progress. */
	uint32 BuildProcessID;

	/**
	* A map of directory paths to shader jobs contained within that directory.
	* One entry per FASTBuild task.
	*/
	class FShaderBatch
	{
		TArray<FShaderCommonCompileJob*> Jobs;
		bool bTransferFileWritten;

	public:
		bool bSuccessfullyCompleted;
		const FString& DirectoryBase;
		const FString& InputFileName;
		const FString& SuccessFileName;
		const FString& OutputFileName;

		int32 BatchIndex;
		int32 DirectoryIndex;

		FString WorkingDirectory;
		FString OutputFileNameAndPath;
		FString SuccessFileNameAndPath;
		FString InputFileNameAndPath;

		FShaderBatch(const FString& InDirectoryBase, const FString& InInputFileName, const FString& InSuccessFileName, const FString& InOutputFileName, int32 InDirectoryIndex, int32 InBatchIndex)
			: bTransferFileWritten(false)
			, bSuccessfullyCompleted(false)
			, DirectoryBase(InDirectoryBase)
			, InputFileName(InInputFileName)
			, SuccessFileName(InSuccessFileName)
			, OutputFileName(InOutputFileName)
		{
			SetIndices(InDirectoryIndex, InBatchIndex);
		}

		void SetIndices(int32 InDirectoryIndex, int32 InBatchIndex);

		void CleanUpFiles(bool keepInputFile);

		inline int32 NumJobs()
		{
			return Jobs.Num();
		}
		inline const TArray<FShaderCommonCompileJob*>& GetJobs() const
		{
			return Jobs;
		}

		void AddJob(FShaderCommonCompileJob* Job);

		void WriteTransferFile();
	};
	TArray<FShaderBatch*> ShaderBatchesInFlight;
	int32 ShaderBatchesInFlightCompleted;
	TArray<FShaderBatch*> ShaderBatchesFull;
	TSparseArray<FShaderBatch*> ShaderBatchesIncomplete;

	/** The full path to the two working directories for FASTBuild shader builds. */
	const FString FASTBuildWorkingDirectory;
	uint32 FASTBuildDirectoryIndex;

	uint64 LastAddTime;
	uint64 StartTime;
	int32 BatchIndexToCreate;
	int32 BatchIndexToFill;

	FDateTime ScriptFileCreationTime;

	void PostCompletedJobsForBatch(FShaderBatch* Batch);

	void GatherResultsFromFASTBuild();

public:
	/** Initialization constructor. */
	FShaderCompileFASTBuildThreadRunnable(class FShaderCompilingManager* InManager);
	virtual ~FShaderCompileFASTBuildThreadRunnable();

	/** Main work loop. */
	virtual int32 CompilingLoop() override;

	static bool IsSupported();
};
