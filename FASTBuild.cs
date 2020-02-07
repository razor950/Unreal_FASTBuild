// Copyright 2018 Yassine Riahi and Liam Flookes. Provided under a MIT License, see license file on github.
// Used to generate a fastbuild .bff file from UnrealBuildTool to allow caching and distributed builds. 
// Tested with Windows 8.1/10, Visual Studio 2019, Unreal Engine 4.23, FastBuild v0.98
// Durango is fully supported (Compiles with VS2015).
// Orbis will likely require some changes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using Tools.DotNETCommon;

namespace UnrealBuildTool
{
	class FASTBuild : ActionExecutor
	{
		/*---- Configurable User settings ----*/

		// Used to specify a non-standard location for the FBuild.exe, for example if you have not added it to your PATH environment variable.
		public static string FBuildExePathOverride = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Extras\\FASTBuild\\FBuild.exe");

        // Path to touch command to keep dependency list file's modified date in sync with compile output
		public static string TouchExePath = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Extras\\FASTBuild\\gnuwin\\bin\\touch.exe");

		// Controls network build distribution
		private const bool bEnableDistribution = true;

		// Controls whether to use caching at all. CachePath and CacheMode are only relevant if this is enabled.
		private bool bEnableCaching = true;

        // Controls whether enable verbose output from FastBuild
		private bool bVerboseMode = false;

		// Controls whether enable detailed logging for distributed compilation
		private bool bDistVerboseMode = true;

		// Controls whether enable fastcancel mode
		private bool bFastCancel = false;

		// Controls whether using FastBuild Mod
		// Following ObjectListNode options are added to better support UE + FastBuild integration
		// - UseLightCache: set to false to disable LightCache per ObjectListNode
		// - WriteInclude:  set to true to force output includes list
		// - WriteIncludeFile: set the file name of output includes list
		// Also CheckCompilerOptions is disable for convenient
		private const bool bEnableFBuildMod = true;

		// UE4Compiler: msvc compiler
		//		- Support distribution build
		//		- Depends on FBuildMod to generate dependency file
		//		- Unverified LightCache support
		// UE4ClFilterCompiler: simulated msvc compiler
		// 		- Support LightCache
		//		- Depends on cl-filter to generate dependency file
		//		- No distribution build support
		private bool bAlwaysUseUE4Compiler = bEnableDistribution && bEnableFBuildMod;

		// Location of the shared cache, it could be a local or network path (i.e: @"\\DESKTOP-BEAST\FASTBuildCache").
		// Only relevant if bEnableCaching is true;
		private string CachePath = "";//@"\\SharedDrive\FASTBuildCache";

		public enum eCacheMode
		{
			ReadWrite, // This machine will both read and write to the cache
			ReadOnly,  // This machine will only read from the cache, use for developer machines when you have centralized build machines
			WriteOnly, // This machine will only write from the cache, use for build machines when you have centralized build machines
		}

		// Cache access mode
		// Only relevant if bEnableCaching is true;
		private eCacheMode CacheMode = eCacheMode.ReadWrite;

		private int NumObjectListAction = 0;
		private int NumLibraryAction = 0;
		private int NumExecutableAction = 0;

		/*--------------------------------------*/

		public override string Name
		{
			get { return "FASTBuild"; }
		}

		public static bool IsAvailable()
		{
			if (FBuildExePathOverride != "")
			{
				return File.Exists(FBuildExePathOverride);
			}

			// Get the name of the FASTBuild executable.
			string fbuild = "fbuild";
			if (BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Win64)
			{
				fbuild = "fbuild.exe";
			}

			// Search the path for it
			string PathVariable = Environment.GetEnvironmentVariable("PATH");
			foreach (string SearchPath in PathVariable.Split(Path.PathSeparator))
			{
				try
				{
					string PotentialPath = Path.Combine(SearchPath, fbuild);
					if (File.Exists(PotentialPath))
					{
						return true;
					}
				}
				catch (ArgumentException)
				{
					// PATH variable may contain illegal characters; just ignore them.
				}
			}
			return false;
		}

		private HashSet<string> ForceLocalCompileModules = new HashSet<string>() { };

		private HashSet<string> ForceNoCachingCompileModules = new HashSet<string>() { };

		private HashSet<string> ForceUE4CompilerCompileModules = new HashSet<string>() {
			"AppleARKit"
            , "AppleVision"
            , "AppleImageUtils"
            , "AVIWriter"
            , "WebBrowser"
		};

		private enum FBBuildType
		{
			Windows,
			XBOne,
			PS4
		}

		private FBBuildType BuildType = FBBuildType.Windows;

		private void DetectBuildType(List<Action> Actions)
		{
			foreach (Action action in Actions)
			{
				if (action.CommandPath.FullName.Contains("orbis"))
				{
					BuildType = FBBuildType.PS4;
					return;
				}
				else if (action.CommandArguments.Contains("Intermediate\\Build\\XboxOne"))
				{
					BuildType = FBBuildType.XBOne;
					return;
				}
				else if (action.CommandPath.FullName.Contains("Microsoft") || action.CommandArguments.Contains("Microsoft")) //Not a great test.
				{					
					BuildType = FBBuildType.Windows;
					return;
				}
			}
		}

		private bool IsMSVC() { return BuildType == FBBuildType.Windows || BuildType == FBBuildType.XBOne; }
		private bool IsPS4() { return BuildType == FBBuildType.PS4; }
		private bool IsXBOnePDBUtil(Action action) { return action.CommandPath.FullName.Contains("XboxOnePDBFileUtil.exe"); }
		private bool IsPS4SymbolTool(Action action) { return action.CommandPath.FullName.Contains("PS4SymbolTool.exe"); }
		private bool IsIntelISPCAction(Action action) { return action.CommandPath.FullName.Contains("ispc.exe"); }
		private string GetCompilerName()
		{
			switch (BuildType)
			{
				default:
				case FBBuildType.XBOne:
				case FBBuildType.Windows: return "UE4Compiler";
				case FBBuildType.PS4: return "UE4PS4Compiler";
			}
		}

		//Run FASTBuild on the list of actions. Relies on fbuild.exe being in the path.
		public override bool ExecuteActions(List<Action> Actions, bool bLogDetailedActionStats)
		{
			bool FASTBuildResult = true;
			if (Actions.Count > 0)
			{
				DetectBuildType(Actions);

				string FASTBuildFilePath = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Intermediate", "Build", "fbuild.bff");

				List<Action> PreBuildLocalExecutorActions = new List<Action>();
				List<Action> PostBuildLocalExecutorActions = new List<Action>();

				if (CreateBffFile(Actions, FASTBuildFilePath, PreBuildLocalExecutorActions, PostBuildLocalExecutorActions))
				{
					Console.WriteLine(string.Format("Executing total {0} actions (Pre:{1}, Post:{2})!"
                        , Actions.Count
                        , PreBuildLocalExecutorActions.Count
                        , PostBuildLocalExecutorActions.Count));

					LocalExecutor localExecutor = new LocalExecutor();

                    if (PreBuildLocalExecutorActions.Count > 0)
					{
						Console.WriteLine(string.Format("Executing {0} PreBuildLocalExecutorActions!", PreBuildLocalExecutorActions.Count));
						FASTBuildResult = localExecutor.ExecuteActions(PreBuildLocalExecutorActions, bLogDetailedActionStats);
					}

					if (FASTBuildResult && FastBuildActionIndices.Count > 0)
					{
						Console.WriteLine(string.Format("Executing {0} FastBuild Actions!", FastBuildActionIndices.Count));
						Console.WriteLine(string.Format("    ObjectList: {0}: ", NumObjectListAction));
						Console.WriteLine(string.Format("       Library: {0}: ", NumLibraryAction));
						Console.WriteLine(string.Format("    Executable: {0}: ", NumExecutableAction));
						FASTBuildResult = ExecuteBffFile(FASTBuildFilePath);
					}

					if (FASTBuildResult && PostBuildLocalExecutorActions.Count > 0)
					{
						Console.WriteLine(string.Format("Executing {0} PostBuildLocalExecutorActions!", PostBuildLocalExecutorActions.Count));
						FASTBuildResult = localExecutor.ExecuteActions(PostBuildLocalExecutorActions, bLogDetailedActionStats);
					}
				}
				else
				{
					FASTBuildResult = false;
				}
			}

			return FASTBuildResult;
		}

		private void AddText(string StringToWrite)
		{
			byte[] Info = new System.Text.UTF8Encoding(true).GetBytes(StringToWrite);
			bffOutputFileStream.Write(Info, 0, Info.Length);
		}


		private string SubstituteEnvironmentVariables(string commandLineString)
		{
			string outputString = commandLineString.Replace("$(DurangoXDK)", "$DurangoXDK$");
			outputString = outputString.Replace("$(SCE_ORBIS_SDK_DIR)", "$SCE_ORBIS_SDK_DIR$");
			outputString = outputString.Replace("$(DXSDK_DIR)", "$DXSDK_DIR$");
			outputString = outputString.Replace("$(CommonProgramFiles)", "$CommonProgramFiles$");

			return outputString;
		}

		private Dictionary<string, string> ParseCommandLineOptions(string CompilerCommandLine, string[] SpecialOptions, bool SaveResponseFile = false, bool SkipInputFile = false)
		{
			Dictionary<string, string> ParsedCompilerOptions = new Dictionary<string, string>();

			// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
			CompilerCommandLine = SubstituteEnvironmentVariables(CompilerCommandLine);

			// Some tricky defines /DTROUBLE=\"\\\" abc  123\\\"\" aren't handled properly by either Unreal or Fastbuild, but we do our best.
			char[] SpaceChar = { ' ' };
			string[] RawTokens = CompilerCommandLine.Trim().Split(' ');
			List<string> ProcessedTokens = new List<string>();
			bool QuotesOpened = false;
			string PartialToken = "";
			string ResponseFilePath = "";

			if (RawTokens.Length >= 1 && RawTokens[0].StartsWith("@\"")) //Response files are in 4.13 by default. Changing VCToolChain to not do this is probably better.
			{
				string responseCommandline = RawTokens[0];

				// If we had spaces inside the response file path, we need to reconstruct the path.
				for (int i = 1; i < RawTokens.Length; ++i)
				{
					responseCommandline += " " + RawTokens[i];
				}

				ResponseFilePath = responseCommandline.Substring(2, responseCommandline.Length - 3); // bit of a bodge to get the @"response" path...
				try
				{
					string ResponseFileText = File.ReadAllText(ResponseFilePath);

					// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
					ResponseFileText = SubstituteEnvironmentVariables(ResponseFileText);

					string[] Separators = { "\n", " ", "\r" };
					if (File.Exists(ResponseFilePath))
						RawTokens = ResponseFileText.Split(Separators, StringSplitOptions.RemoveEmptyEntries); //Certainly not ideal 
				}
				catch (Exception e)
				{
					Console.WriteLine("Looks like a response file in: " + CompilerCommandLine + ", but we could not load it! " + e.Message);
					ResponseFilePath = "";
				}
			}

			// Raw tokens being split with spaces may have split up some two argument options and 
			// paths with multiple spaces in them also need some love
			for (int i = 0; i < RawTokens.Length; ++i)
			{
				string Token = RawTokens[i];
				if (string.IsNullOrEmpty(Token))
				{
					if (ProcessedTokens.Count > 0 && QuotesOpened)
					{
						string CurrentToken = ProcessedTokens.Last();
						CurrentToken += " ";
					}

					continue;
				}

				int numQuotes = 0;
				// Look for unescaped " symbols, we want to stick those strings into one token.
				for (int j = 0; j < Token.Length; ++j)
				{
					if (Token[j] == '\\') //Ignore escaped quotes
						++j;
					else if (Token[j] == '"')
						numQuotes++;
				}

				// Defines can have escaped quotes and other strings inside them
				// so we consume tokens until we've closed any open unescaped parentheses.
				if ((Token.StartsWith("/D") || Token.StartsWith("-D")) && !QuotesOpened)
				{
					if (numQuotes == 0 || numQuotes == 2)
					{
						ProcessedTokens.Add(Token);
					}
					else
					{
						PartialToken = Token;
						++i;
						bool AddedToken = false;
						for (; i < RawTokens.Length; ++i)
						{
							string NextToken = RawTokens[i];
							if (string.IsNullOrEmpty(NextToken))
							{
								PartialToken += " ";
							}
							else if (!NextToken.EndsWith("\\\"") && NextToken.EndsWith("\"")) //Looking for a token that ends with a non-escaped "
							{
								ProcessedTokens.Add(PartialToken + " " + NextToken);
								AddedToken = true;
								break;
							}
							else
							{
								PartialToken += " " + NextToken;
							}
						}
						if (!AddedToken)
						{
							Console.WriteLine("Warning! Looks like an unterminated string in tokens. Adding PartialToken and hoping for the best. Command line: " + CompilerCommandLine);
							ProcessedTokens.Add(PartialToken);
						}
					}
					continue;
				}

				if (!QuotesOpened)
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						PartialToken = Token + " ";
						QuotesOpened = true;
					}
					else
					{
						ProcessedTokens.Add(Token);
					}
				}
				else
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						ProcessedTokens.Add(PartialToken + Token);
						QuotesOpened = false;
					}
					else
					{
						PartialToken += Token + " ";
					}
				}
			}

			//Processed tokens should now have 'whole' tokens, so now we look for any specified special options
			foreach (string specialOption in SpecialOptions)
			{
				for (int i = 0; i < ProcessedTokens.Count; ++i)
				{
					if (ProcessedTokens[i] == specialOption && i + 1 < ProcessedTokens.Count)
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i + 1];
						ProcessedTokens.RemoveRange(i, 2);
						break;
					}
					else if (ProcessedTokens[i].StartsWith(specialOption))
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i].Replace(specialOption, null);
						ProcessedTokens.RemoveAt(i);
						break;
					}
				}
			}

			//The search for the input file... we take the first non-argument we can find
			if (!SkipInputFile)
			{
				for (int i = 0; i < ProcessedTokens.Count; ++i)
				{
					string Token = ProcessedTokens[i];
					if (Token.Length == 0)
					{
						continue;
					}

					if (Token == "/I" || Token == "/l" || Token == "/D" || Token == "-D" || Token == "-x" || Token == "-include") // Skip tokens with values, I for cpp includes, l for resource compiler includes
					{
						++i;
					}
					else if (!Token.StartsWith("/") && !Token.StartsWith("-") && !Token.StartsWith("\"-"))
					{
						ParsedCompilerOptions["InputFile"] = Token;
						ProcessedTokens.RemoveAt(i);
						break;
					}
				}
			}

			ParsedCompilerOptions["OtherOptions"] = string.Join(" ", ProcessedTokens) + " ";

			if (SaveResponseFile && !string.IsNullOrEmpty(ResponseFilePath))
			{
				ParsedCompilerOptions["@"] = ResponseFilePath;
			}

			return ParsedCompilerOptions;
		}

		private List<Action> SortActions(List<Action> InActions)
		{
			List<Action> Actions = InActions;

			int NumSortErrors = 0;
			for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
			{
				Action Action = InActions[ActionIndex];
				foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
				{
					if (InActions.Contains(PrerequisiteAction))
					{
						int DepIndex = InActions.IndexOf(PrerequisiteAction);
						if (DepIndex > ActionIndex)
						{
							NumSortErrors++;
						}
					}
				}
			}
			if (NumSortErrors > 0)
			{
				Actions = new List<Action>();
				var UsedActions = new HashSet<int>();
				for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
				{
					if (UsedActions.Contains(ActionIndex))
					{
						continue;
					}
					Action Action = InActions[ActionIndex];
					foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
					{
						if (InActions.Contains(PrerequisiteAction))
						{
							int DepIndex = InActions.IndexOf(PrerequisiteAction);
							if (UsedActions.Contains(DepIndex))
							{
								continue;
							}
							Actions.Add(PrerequisiteAction);
							UsedActions.Add(DepIndex);
						}
					}
					Actions.Add(Action);
					UsedActions.Add(ActionIndex);
				}
				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];
					foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
					{
						if (Actions.Contains(PrerequisiteAction))
						{
							int DepIndex = Actions.IndexOf(PrerequisiteAction);
							if (DepIndex > ActionIndex)
							{
								Console.WriteLine("Action is not topologically sorted.");
								Console.WriteLine("  {0} {1}", Action.CommandPath, Action.CommandArguments);
								Console.WriteLine("Dependency");
								Console.WriteLine("  {0} {1}", PrerequisiteAction.CommandPath, PrerequisiteAction.CommandArguments);
								throw new BuildException("Cyclical Dependency in action graph.");
							}
						}
					}
				}
			}

			return Actions;
		}

		private string GetOptionValue(Dictionary<string, string> OptionsDictionary, string Key, Action Action, bool ProblemIfNotFound = false)
		{
			string Value = string.Empty;
			if (OptionsDictionary.TryGetValue(Key, out Value))
			{
				return Value.Trim(new Char[] { '\"' });
			}

			if (ProblemIfNotFound)
			{
				Console.WriteLine("We failed to find " + Key + ", which may be a problem.");
				Console.WriteLine("Action.CommandArguments: " + Action.CommandArguments);
			}

			return Value;
		}

		public string GetRegistryValue(string keyName, string valueName, object defaultValue)
		{
			object returnValue = (string)Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = (string)Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\Wow6432Node\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			return defaultValue.ToString();
		}

		private void WriteEnvironmentSetup()
		{
			VCEnvironment VCEnv = null;

			try
			{
				// This may fail if the caller emptied PATH; we try to ignore the problem since
				// it probably means we are building for another platform.
				if (BuildType == FBBuildType.Windows)
				{
					WindowsCompiler windowsCompiler = WindowsCompiler.VisualStudio2019;
					VCEnv = VCEnvironment.Create(windowsCompiler, UnrealTargetPlatform.Win64, WindowsArchitecture.x64, null, null);
				}
				else if (BuildType == FBBuildType.XBOne)
				{
					// If you have XboxOne source access, uncommenting the line below will be better for selecting the appropriate version of the compiler.
					// Translate the XboxOne compiler to the right Windows compiler to set the VC environment vars correctly...
					//WindowsCompiler windowsCompiler = XboxOnePlatform.GetDefaultCompiler() == XboxOneCompiler.VisualStudio2015 ? WindowsCompiler.VisualStudio2015 : WindowsCompiler.VisualStudio2017;
					WindowsCompiler windowsCompiler = WindowsCompiler.VisualStudio2017;
					VCEnv = VCEnvironment.Create(windowsCompiler, UnrealTargetPlatform.Win64, WindowsArchitecture.x64, null, null);
				}
			}
			catch (Exception)
			{
				Console.WriteLine("Failed to get Visual Studio environment.");
			}

			// Copy environment into a case-insensitive dictionary for easier key lookups
			Dictionary<string, string> envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
			{
				envVars[(string)entry.Key] = (string)entry.Value;
			}

			if (envVars.ContainsKey("CommonProgramFiles"))
			{
				AddText("#import CommonProgramFiles\n");
			}

			if (envVars.ContainsKey("DXSDK_DIR"))
			{
				AddText("#import DXSDK_DIR\n");
			}

			if (envVars.ContainsKey("DurangoXDK"))
			{
				AddText("#import DurangoXDK\n");
			}

			if (VCEnv != null)
			{
				string platformVersionNumber = "VSVersionUnknown";

				switch (VCEnv.Compiler)
				{
// 					case WindowsCompiler.VisualStudio2015:
// 						platformVersionNumber = "140";
// 						break;

					case WindowsCompiler.VisualStudio2017:
						// For now we are working with the 140 version, might need to change to 141 or 150 depending on the version of the Toolchain you chose
						// to install
						platformVersionNumber = "140";
						break;

					case WindowsCompiler.VisualStudio2019:
						// For now we are working with the 140 version, might need to change to 141 or 150 depending on the version of the Toolchain you chose
						// to install
						platformVersionNumber = "140";
						break;

					default:
						string exceptionString = "Error: Unsupported Visual Studio Version.";
						Console.WriteLine(exceptionString);
						throw new BuildException(exceptionString);
				}

				FileReference ResourceCompilerPath = FileReference.Combine(VCEnv.WindowsSdkDir, "bin", VCEnv.WindowsSdkVersion.ToString(), "x64", "rc.exe");

				AddText(string.Format(".WindowsSDKBasePath = '{0}'\n", VCEnv.WindowsSdkDir));

				AddText("Compiler('UE4ResourceCompiler') \n{\n");
				AddText(string.Format("\t.Executable = '{0}'\n", ResourceCompilerPath));
                AddText("\t.CompilerFamily  = 'custom'\n");
                AddText("}\n\n");


				string ClFilterFilePath = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Build", "Windows", "cl-filter", "cl-filter.exe");

				AddText("Compiler('UE4ClFilterCompiler') \n{\n");
				AddText(string.Format("\t.Executable = '{0}'\n", ClFilterFilePath));
				AddText("\t.CompilerFamily  = 'msvc'\n");
				AddText("\t.AllowDistribution  = false\n");
				{
					const string UseLightCacheString = bEnableFBuildMod ? "true" : "false";
					AddText(string.Format("\t.UseLightCache_Experimental = {0}\n", UseLightCacheString));
				}
				AddText("}\n\n");

				DirectoryReference VCToolPath64 = VCEnv.CompilerPath.Directory;

				AddText("Compiler('UE4Compiler') \n{\n");
    			AddText(string.Format("\t.Root = '{0}'\n", VCToolPath64));
                AddText("\t.Executable = '$Root$/cl.exe'\n");

				{
					const string UseLightCacheString = bEnableFBuildMod ? "true" : "false";
					AddText(string.Format("\t.UseLightCache_Experimental = {0}\n", UseLightCacheString));
				}

				AddText("\t.ExtraFiles =\n\t{\n");
				AddText("\t\t'$Root$/c1.dll'\n");
				AddText("\t\t'$Root$/c1xx.dll'\n");
				AddText("\t\t'$Root$/c2.dll'\n");

                if (File.Exists(VCToolPath64 + "/1033/clui.dll")) //Check English first...
				{
					AddText("\t\t'$Root$/1033/clui.dll'\n");
					AddText("\t\t'$Root$/1033/mspft140ui.dll'\n"); // Localized messages for static analysis
				}
				else
				{
					var numericDirectories = Directory.GetDirectories(VCToolPath64.ToString()).Where(d => Path.GetFileName(d).All(char.IsDigit));
					var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
					if (cluiDirectories.Any())
					{
						AddText(string.Format("\t\t'$Root$/{0}/clui.dll'\n", Path.GetFileName(cluiDirectories.First())));
						AddText(string.Format("\t\t'$Root$/{0}/mspft140ui.dll'\n", Path.GetFileName(cluiDirectories.First())));
					}
				}
				AddText("\t\t'$Root$/mspdbsrv.exe'\n");
				AddText("\t\t'$Root$/mspdbcore.dll'\n");

				if (File.Exists(VCToolPath64 + "/tbbmalloc.dll")) // vs2019+
				{
					AddText("\t\t'$Root$/tbbmalloc.dll'\n");
				}

				AddText(string.Format("\t\t'$Root$/mspft{0}.dll'\n", platformVersionNumber));
				AddText(string.Format("\t\t'$Root$/msobj{0}.dll'\n", platformVersionNumber));
				AddText(string.Format("\t\t'$Root$/mspdb{0}.dll'\n", platformVersionNumber));
				AddText(string.Format("\t\t'$Root$/msvcp{0}.dll'\n", platformVersionNumber));
				AddText(string.Format("\t\t'$Root$/vcruntime{0}.dll'\n", platformVersionNumber));

				AddText("\t}\n"); //End extra files

				AddText("}\n\n"); //End compiler
			}

			if (envVars.ContainsKey("SCE_ORBIS_SDK_DIR"))
			{
				AddText(string.Format(".SCE_ORBIS_SDK_DIR = '{0}'\n", envVars["SCE_ORBIS_SDK_DIR"]));
				AddText(string.Format(".PS4BasePath = '{0}/host_tools/bin'\n\n", envVars["SCE_ORBIS_SDK_DIR"]));
				AddText("Compiler('UE4PS4Compiler') \n{\n");
				AddText("\t.Executable = '$PS4BasePath$/orbis-clang.exe'\n");
				AddText("\t.ExtraFiles = '$PS4BasePath$/orbis-snarl.exe'\n");
				AddText("}\n\n");
			}

			AddText("Settings \n{\n");

			// Optional cachePath user setting
			if (bEnableCaching && CachePath != "")
			{
				AddText(string.Format("\t.CachePath = '{0}'\n", CachePath));
			}

			bool bDumpEnvironment = false;
            if (bDumpEnvironment)
			{
				//Start Environment
				AddText("\t.Environment = \n\t{\n");
				if (VCEnv != null)
				{
					AddText(string.Format("\t\t\"PATH={0}\\..\\..\\..\\..\\Common7\\IDE\\;{1}\",\n", VCEnv.ToolChainDir.ToString(), VCEnv.CompilerPath.Directory));
					if (VCEnv.IncludePaths.Count() > 0)
					{
						AddText(string.Format("\t\t\"INCLUDE={0}\",\n", String.Join(";", VCEnv.IncludePaths.Select(x => x))));
					}

					if (VCEnv.LibraryPaths.Count() > 0)
					{
						AddText(string.Format("\t\t\"LIB={0}\",\n", String.Join(";", VCEnv.LibraryPaths.Select(x => x))));
					}
				}
				if (envVars.ContainsKey("TMP"))
					AddText(string.Format("\t\t\"TMP={0}\",\n", envVars["TMP"]));
				if (envVars.ContainsKey("SystemRoot"))
					AddText(string.Format("\t\t\"SystemRoot={0}\",\n", envVars["SystemRoot"]));
				if (envVars.ContainsKey("INCLUDE"))
					AddText(string.Format("\t\t\"INCLUDE={0}\",\n", envVars["INCLUDE"]));
				if (envVars.ContainsKey("LIB"))
					AddText(string.Format("\t\t\"LIB={0}\",\n", envVars["LIB"]));

				AddText("\t}\n"); //End environment
			}
			AddText("}\n\n"); //End Settings
		}

		private bool AddCompileAction(Action Action, int ActionIndex, List<int> DependencyIndices)
		{
			string CommandArguments = Action.CommandArguments;

			if (IsIntelISPCAction(Action))
			{
				string[] ISPCSpecialCompilerOptions = { "-h", "-o" };
				var ISPCParsedCompilerOptions = ParseCommandLineOptions(CommandArguments, ISPCSpecialCompilerOptions);

				bool bHeaderOutput = true;
				string ISPCOutputFileName = GetOptionValue(ISPCParsedCompilerOptions, "-h", Action, ProblemIfNotFound: false);

				if (string.IsNullOrEmpty(ISPCOutputFileName))
				{
					bHeaderOutput = false;
					ISPCOutputFileName = GetOptionValue(ISPCParsedCompilerOptions, "-o", Action, ProblemIfNotFound: true);
				}

				if (string.IsNullOrEmpty(ISPCOutputFileName))
				{
					Console.WriteLine("We have no OutputFileName for IntelISPC. Bailing.");
					return false;
				}

				AddText(string.Format("Exec('Action_{0}')\n{{\n", ActionIndex));
				AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
				AddText(string.Format("\t.ExecArguments = '\"%1\" {0} \"%2\" {1}'\n", bHeaderOutput ? "-h" : "-o", GetOptionValue(ISPCParsedCompilerOptions, "OtherOptions", Action)));
				AddText(string.Format("\t.ExecInput = {{ {0} }} \n", ISPCParsedCompilerOptions["InputFile"]));
				AddText(string.Format("\t.ExecOutput = '{0}' \n", ISPCOutputFileName));
				AddText(string.Format("\t.PreBuildDependencies = {{ {0} }} \n", ISPCParsedCompilerOptions["InputFile"]));
				AddText(string.Format("}}\n\n"));

				return true;
			}

			string CompilerName = GetCompilerName();
			string ResponseFilePath = "";
			string DependenciesListFile = "";
			bool bTouchNode = false;

			if (Action.CommandPath.FullName.Contains("rc.exe"))
			{
				CompilerName = "UE4ResourceCompiler";
			}
			else if (Action.CommandPath.FullName.Contains("cl-filter.exe"))
			{
				CompilerName = "UE4ClFilterCompiler";

				string[] ResponseFileOptions = { "@" };
				var ParsedResponseFileOptions = ParseCommandLineOptions(CommandArguments, ResponseFileOptions);
				ResponseFilePath = GetOptionValue(ParsedResponseFileOptions, "@", Action);

				if (string.IsNullOrEmpty(ResponseFilePath))
				{
					Console.WriteLine("We have no ResponseFile for cl-filter.exe. Bailing.");
					return false;
				}

				string[] CFSpecialCompilerOptions = { "-dependencies=" };
				var CFParsedCompilerOptions = ParseCommandLineOptions(Action.CommandArguments, CFSpecialCompilerOptions);
				DependenciesListFile = GetOptionValue(CFParsedCompilerOptions, "-dependencies=", Action, ProblemIfNotFound: true);

				if (string.IsNullOrEmpty(DependenciesListFile))
				{
					Console.WriteLine("We have no DependenciesListFile for cl-filter.exe. Bailing.");
					return false;
				}

				if (bAlwaysUseUE4Compiler)
				{
					CompilerName = "UE4Compiler";
				}
				// fallback to UE4Compiler
				else if (!File.Exists(DependenciesListFile) && bEnableFBuildMod)
				{
					//Console.WriteLine(string.Format("Missing {0}, force write includes from fastbuild.", DependenciesListFile));
                    CompilerName = "UE4Compiler";
				}
				//else
				{
					bTouchNode = true;
				}

				CommandArguments = "@\"" + ResponseFilePath + "\"";
				//Console.WriteLine("  => CommandArgs Now:" + CommandArguments);
			}

			string[] SpecialCompilerOptions = { "/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o" };
			var ParsedCompilerOptions = ParseCommandLineOptions(CommandArguments, SpecialCompilerOptions);

			string OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, IsMSVC() ? "/Fo" : "-o", Action, ProblemIfNotFound: !IsMSVC());

			if (IsMSVC() && string.IsNullOrEmpty(OutputObjectFileName)) // Didn't find /Fo, try /fo
			{
				OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/fo", Action, ProblemIfNotFound: true);
			}

			if (string.IsNullOrEmpty(OutputObjectFileName)) //No /Fo or /fo, we're probably in trouble.
			{
				Console.WriteLine("We have no OutputObjectFileName. Bailing.");
				return false;
			}

			string IntermediatePath = Path.GetDirectoryName(OutputObjectFileName);
			if (string.IsNullOrEmpty(IntermediatePath))
			{
				Console.WriteLine("We have no IntermediatePath. Bailing.");
				Console.WriteLine("Our Action.CommandArguments were: " + Action.CommandArguments);
				return false;
			}

			string InputFile = GetOptionValue(ParsedCompilerOptions, "InputFile", Action, ProblemIfNotFound: true);
			if (string.IsNullOrEmpty(InputFile))
			{
				Console.WriteLine("We have no InputFile. Bailing.");
				return false;
			}

			if (!bAlwaysUseUE4Compiler)
			{
				bool bForceUseUE4Compile = false;

				foreach (var it in ForceUE4CompilerCompileModules)
				{
					if (Path.GetFullPath(InputFile).Contains(it))
					{
						bForceUseUE4Compile = true;
						break;
					}
				}

				if (bForceUseUE4Compile)
				{
					CompilerName = "UE4Compiler";
				}
			}

			++NumObjectListAction;

			bool bExecForPCH = CompilerName == "UE4ClFilterCompiler";
			if (bExecForPCH && ResponseFilePath.EndsWith(".pch.response"))
			{
				AddText(string.Format("Exec('Action_{0}')\n{{\n", ActionIndex));
				AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
				AddText(string.Format("\t.ExecArguments = '{0}'\n", Action.CommandArguments));
				AddText(string.Format("\t.ExecInput = {{ '{0}' }} \n", InputFile));
				AddText(string.Format("\t.ExecOutput = '{0}' \n", OutputObjectFileName));
				if (DependencyIndices.Count > 0)
				{
					List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
					AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(",", DependencyNames.ToArray())));
				}
				AddText(string.Format("}}\n\n"));
			}
            else
			{
				AddText(string.Format("ObjectList('Action_{0}')\n{{\n", ActionIndex));
				AddText(string.Format("\t.Compiler = '{0}' \n", CompilerName));
				AddText(string.Format("\t.CompilerInputFiles = \"{0}\"\n", InputFile));
				AddText(string.Format("\t.CompilerOutputPath = \"{0}\"\n", IntermediatePath));

				bool bSkipDistribution = false;
				foreach (var it in ForceLocalCompileModules)
				{
					if (Path.GetFullPath(InputFile).Contains(it))
					{
						bSkipDistribution = true;
						break;
					}
				}

				if (!Action.bCanExecuteRemotely || !Action.bCanExecuteRemotelyWithSNDBS || bSkipDistribution)
				{
					AddText(string.Format("\t.AllowDistribution = false\n"));
				}

				bool bNoCaching = false;
				foreach (var it in ForceNoCachingCompileModules)
				{
					if (Path.GetFullPath(InputFile).Contains(it))
					{
						bNoCaching = true;
						break;
					}
				}

				if (bNoCaching)
				{
					AddText(string.Format("\t.AllowCaching = false\n"));
				}

				if (CompilerName == "UE4Compiler" && bEnableFBuildMod)
				{
					AddText(string.Format("\t.UseLightCache = false\n"));
					AddText(string.Format("\t.WriteInclude = true\n"));
					if (!string.IsNullOrEmpty(DependenciesListFile))
					{
						AddText(string.Format("\t.WriteIncludeFile = '{0}'\n", DependenciesListFile));
					}
				}

				string OtherCompilerOptions = GetOptionValue(ParsedCompilerOptions, "OtherOptions", Action);
				if (IsMSVC() && CompilerName == "UE4Compiler")
				{
					OtherCompilerOptions += "/wd4668";
					OtherCompilerOptions = OtherCompilerOptions.Replace("/we4668", "");
				}

				string CompilerOutputExtension = (InputFile.EndsWith(".c") ? ".c" : (InputFile.EndsWith(".cc") ? ".cc" : ".cpp")) + (IsMSVC() ? ".obj" : ".o");

				if (CompilerName == "UE4ResourceCompiler")
				{
					AddText(string.Format("\t.CompilerOptions = '{0} /fo\"%2\" \"%1\" '\n", OtherCompilerOptions));
					CompilerOutputExtension = Path.GetExtension(InputFile) + ".res";
				}
				else if (CompilerName == "UE4ClFilterCompiler")
				{
					AddText(string.Format("\t.CompilerOptions = '{0}'\n", Action.CommandArguments));
				}
				else // CompilerName == "UE4Compiler"
				{
					if (ParsedCompilerOptions.ContainsKey("/Yc")) //Create PCH
					{
						string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yc", Action, ProblemIfNotFound: true);
						string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);

						AddText(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /Fp\"{0}\" /Yu\"{1}\" {2} '\n", PCHOutputFile, PCHIncludeHeader, OtherCompilerOptions));

						AddText(string.Format("\t.PCHOptions = '\"%1\" /Fp\"%2\" /Yc\"{0}\" {1} /Fo\"{2}\"'\n", PCHIncludeHeader, OtherCompilerOptions, OutputObjectFileName));
						AddText(string.Format("\t.PCHInputFile = \"{0}\"\n", InputFile));
						AddText(string.Format("\t.PCHOutputFile = \"{0}\"\n", PCHOutputFile));
						CompilerOutputExtension = ".obj";
					}
					else if (ParsedCompilerOptions.ContainsKey("/Yu")) //Use PCH
					{
						string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yu", Action, ProblemIfNotFound: true);
						string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);
						string PCHToForceInclude = PCHOutputFile.Replace(".pch", "");
						AddText(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /Fp\"{0}\" /Yu\"{1}\" /FI\"{2}\" {3} '\n", PCHOutputFile, PCHIncludeHeader, PCHToForceInclude, OtherCompilerOptions));
					}
					else
					{
						AddText(string.Format("\t.CompilerOptions = '{0} {1}\"%2\" \"%1\" '\n", OtherCompilerOptions, IsMSVC() ? "/Fo" : "-o "));
					}
				}

				AddText(string.Format("\t.CompilerOutputExtension = '{0}' \n", CompilerOutputExtension));

				if (DependencyIndices.Count > 0)
				{
					List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
					AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(",", DependencyNames.ToArray())));
				}

				AddText(string.Format("}}\n\n"));
			}

            if (bTouchNode)
			{
				// Touch
				List<string> ProducedItems = Action.ProducedItems.ConvertAll(x => string.Format("{0}", x.FullName));
				if (ProducedItems.Count > 0)
				{
					string ProducedItem = ProducedItems[0];
					{
						AddText(string.Format("Exec('Action_{0}_Touch')\n{{ \n", ActionIndex));
						AddText(string.Format("\t.ExecExecutable = '{0}' \n", TouchExePath));
						AddText(string.Format("\t.ExecArguments = '\"%1\" -c -r \"{0}\"'\n", ProducedItem));
						AddText(string.Format("\t.ExecInput = '{0}' \n", DependenciesListFile));
						AddText(string.Format("\t.ExecOutput = '{0}.dummy' \n", DependenciesListFile));
						AddText(string.Format("\t.ExecUseStdOutAsOutput = true \n", DependenciesListFile));
						AddText(string.Format("\t.PreBuildDependencies = {{ 'Action_{0}' }} \n", ActionIndex));
						//AddText(string.Format("\t.PreBuildDependencies = {{ 'Action_{0}', '{1}' }} \n", ActionIndex, DependenciesListFile));
						AddText(string.Format("}}\n\n"));
					}

					FastBuildTouchActionIndices.Add(ActionIndex);
				}
			}

			return true;
		}

		private bool AddLinkAction(List<Action> Actions, int ActionIndex, List<int> DependencyIndices)
		{
			Action Action = Actions[ActionIndex];
			string CommandArguments = Action.CommandArguments;

			if (Action.CommandPath.FullName.Contains("link-filter.exe"))
			{
    			string[] ResponseFileOptions = { "@" };
				var ParsedResponseFileOptions = ParseCommandLineOptions(CommandArguments, ResponseFileOptions);
				string ResponseFile = GetOptionValue(ParsedResponseFileOptions, "@", Action);
				CommandArguments = "@\"" + ResponseFile + "\"";
			}

			string[] SpecialLinkerOptions = { "/OUT:", "@", "-o" };
			var ParsedLinkerOptions = ParseCommandLineOptions(CommandArguments, SpecialLinkerOptions, SaveResponseFile: true, SkipInputFile: Action.CommandPath.FullName.Contains("orbis-clang"));

			string OutputFile;

			if (IsXBOnePDBUtil(Action))
			{
				OutputFile = ParsedLinkerOptions["OtherOptions"].Trim(' ').Trim('"');
			}
			else if (IsMSVC())
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "/OUT:", Action, ProblemIfNotFound: true);
			}
			else //PS4
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "-o", Action, ProblemIfNotFound: false);
				if (string.IsNullOrEmpty(OutputFile))
				{
					OutputFile = GetOptionValue(ParsedLinkerOptions, "InputFile", Action, ProblemIfNotFound: true);
				}
			}

			if (string.IsNullOrEmpty(OutputFile))
			{
				Console.WriteLine("Failed to find output file. Bailing.");
				return false;
			}

			string ResponseFilePath = GetOptionValue(ParsedLinkerOptions, "@", Action);
			string OtherCompilerOptions = GetOptionValue(ParsedLinkerOptions, "OtherOptions", Action);

			List<int> PrebuildDependencies = new List<int>();

			if (IsXBOnePDBUtil(Action))
			{
				AddText(string.Format("Exec('Action_{0}')\n{{\n", ActionIndex));
				AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
				AddText(string.Format("\t.ExecArguments = '{0}'\n", Action.CommandArguments));
				AddText(string.Format("\t.ExecInput = {{ {0} }} \n", ParsedLinkerOptions["InputFile"]));
				AddText(string.Format("\t.ExecOutput = '{0}' \n", OutputFile));
				AddText(string.Format("\t.PreBuildDependencies = {{ {0} }} \n", ParsedLinkerOptions["InputFile"]));
				AddText(string.Format("}}\n\n"));
			}
			else if (IsPS4SymbolTool(Action))
			{
				string searchString = "-map=\"";
				int execArgumentStart = Action.CommandArguments.LastIndexOf(searchString) + searchString.Length;
				int execArgumentEnd = Action.CommandArguments.IndexOf("\"", execArgumentStart);
				string ExecOutput = Action.CommandArguments.Substring(execArgumentStart, execArgumentEnd - execArgumentStart);

				AddText(string.Format("Exec('Action_{0}')\n{{\n", ActionIndex));
				AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
				AddText(string.Format("\t.ExecArguments = '{0}'\n", Action.CommandArguments));
				AddText(string.Format("\t.ExecOutput = '{0}'\n", ExecOutput));
				AddText(string.Format("\t.PreBuildDependencies = {{ 'Action_{0}' }} \n", ActionIndex - 1));
				AddText(string.Format("}}\n\n"));
			}
			else if (Action.CommandPath.FullName.Contains("link-filter.exe") || Action.CommandPath.FullName.Contains("lib.exe") || Action.CommandPath.FullName.Contains("orbis-snarl"))
			{
				++NumLibraryAction;

				bool bUseExec = true;
				if (bUseExec)
				{
					AddText(string.Format("Exec('Action_{0}')\n{{\n", ActionIndex));
					AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
					AddText(string.Format("\t.ExecArguments = '{0}'\n", Action.CommandArguments));
					AddText(string.Format("\t.ExecOutput = '{0}' \n", OutputFile));
					if (DependencyIndices.Count > 0)
					{
						List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("\t\t'Action_{0}', ;{1}", x, Actions[x].StatusDescription));
						AddText(string.Format("\t.PreBuildDependencies = {{\n{0}\n\t}} \n", string.Join("\n", DependencyNames.ToArray())));

					}
					AddText(string.Format("}}\n\n"));

					return true;
				}
				else
				{
                    if (DependencyIndices.Count > 0)
                    {
	                    for (int i = 0; i < DependencyIndices.Count; ++i) //Don't specify pch or resource files, they have the wrong name and the response file will have them anyways.
	                    {
		                    int depIndex = DependencyIndices[i];
		                    foreach (FileItem item in Actions[depIndex].ProducedItems)
		                    {
			                    if (item.ToString().Contains(".pch") || item.ToString().Contains(".res"))
			                    {
				                    DependencyIndices.RemoveAt(i);
				                    i--;
				                    PrebuildDependencies.Add(depIndex);
				                    break;
			                    }
		                    }
	                    }
                    }

					AddText(string.Format("Library('Action_{0}')\n{{\n", ActionIndex));
					AddText(string.Format("\t.Compiler = '{0}'\n", GetCompilerName()));
					if (IsMSVC())
						AddText(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c'\n"));
					else
						AddText(string.Format("\t.CompilerOptions = '\"%1\" -o \"%2\" -c'\n"));
					AddText(string.Format("\t.CompilerOutputPath = \"{0}\"\n", Path.GetDirectoryName(OutputFile)));
					AddText(string.Format("\t.Librarian = '{0}' \n", Action.CommandPath));

					if (!string.IsNullOrEmpty(ResponseFilePath))
					{
						if (IsMSVC())
							// /ignore:4042 to turn off the linker warning about the output option being present twice (command-line + rsp file)
							// /ignore:4206 to turn off precompiled type information not found
							AddText(string.Format("\t.LibrarianOptions = ' /OUT:\"%2\" /ignore:4042 @\"{0}\" \"%1\"' \n", ResponseFilePath));
						else if (IsPS4())
							AddText(string.Format("\t.LibrarianOptions = '\"%2\" @\"%1\"' \n", ResponseFilePath));
						else
							AddText(string.Format("\t.LibrarianOptions = '\"%2\" @\"%1\" {0}' \n", OtherCompilerOptions));
					}
					else
					{
						if (IsMSVC())
							AddText(string.Format("\t.LibrarianOptions = ' /OUT:\"%2\" {0} \"%1\"' \n", OtherCompilerOptions));
					}

					if (DependencyIndices.Count > 0)
					{
						List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));

						if (IsPS4())
							AddText(string.Format("\t.LibrarianAdditionalInputs = {{ '{0}' }} \n", ResponseFilePath)); // Hack...Because FastBuild needs at least one Input file
						else if (!string.IsNullOrEmpty(ResponseFilePath))
							AddText(string.Format("\t.LibrarianAdditionalInputs = {{ {0} }} \n", DependencyNames[0])); // Hack...Because FastBuild needs at least one Input file
						else if (IsMSVC())
							AddText(string.Format("\t.LibrarianAdditionalInputs = {{ {0} }} \n", string.Join(",", DependencyNames.ToArray())));

						PrebuildDependencies.AddRange(DependencyIndices);
					}
					else
					{
						string InputFile = GetOptionValue(ParsedLinkerOptions, "InputFile", Action, ProblemIfNotFound: true);
						if (InputFile != null && InputFile.Length > 0)
							AddText(string.Format("\t.LibrarianAdditionalInputs = {{ '{0}' }} \n", InputFile));
					}

					if (PrebuildDependencies.Count > 0)
					{
						List<string> PrebuildDependencyNames = PrebuildDependencies.ConvertAll(x => string.Format("'Action_{0}'", x));
						AddText(string.Format("\t.PreBuildDependencies = {{ {0} }} \n", string.Join(",", PrebuildDependencyNames.ToArray())));
					}

					AddText(string.Format("\t.LibrarianOutput = '{0}' \n", OutputFile));
					AddText(string.Format("}}\n\n"));
				}
			}
			else if (Action.CommandPath.FullName.Contains("link.exe") || Action.CommandPath.FullName.Contains("orbis-clang"))
			{
				++NumExecutableAction;

				bool bUseExec = true;
                if (bUseExec)
				{
					AddText(string.Format("Exec('Action_{0}')\n{{\n", ActionIndex));
					AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
					AddText(string.Format("\t.ExecArguments = '{0}'\n", Action.CommandArguments));
					AddText(string.Format("\t.ExecOutput = '{0}' \n", OutputFile));
					if (DependencyIndices.Count > 0)
					{
						List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("\t\t'Action_{0}', ;{1}", x, Actions[x].StatusDescription));
						AddText(string.Format("\t.PreBuildDependencies = {{\n{0}\n\t}} \n", string.Join("\n", DependencyNames.ToArray())));

					}
					AddText(string.Format("}}\n\n"));
				}
                else
				{
					AddText(string.Format("Executable('Action_{0}')\n{{ \n", ActionIndex));
					AddText(string.Format("\t.Linker = '{0}' \n", Action.CommandPath));

					AddText(string.Format("\t.Libraries = {{ '{0}' }} \n", ResponseFilePath));
					if (IsMSVC())
					{
						if (BuildType == FBBuildType.XBOne)
						{
							AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{0}\" {1} ' \n", ResponseFilePath, OtherCompilerOptions)); // The TLBOUT is a huge bodge to consume the %1.
						}
						else
						{
							// /ignore:4042 to turn off the linker warning about the output option being present twice (command-line + rsp file)
							AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /ignore:4042 /Out:\"%2\" @\"{0}\" ' \n", ResponseFilePath)); // The TLBOUT is a huge bodge to consume the %1.
						}
					}
					else
						AddText(string.Format("\t.LinkerOptions = '{0} -o \"%2\" @\"%1\"' \n", OtherCompilerOptions)); // The MQ is a huge bodge to consume the %1.

					AddText(string.Format("\t.LinkerOutput = '{0}' \n", OutputFile));

					if (DependencyIndices.Count > 0)
					{
						List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("\t\t'Action_{0}', ;{1}", x, Actions[x].StatusDescription));
						AddText(string.Format("\t.PreBuildDependencies = {{\n{0}\n\t}} \n", string.Join("\n", DependencyNames.ToArray())));
					}

					AddText(string.Format("}}\n\n"));
				}
			}

			return true;
		}

		private FileStream bffOutputFileStream = null;

		private List<int> FastBuildActionIndices = new List<int>();
		private List<int> FastBuildTouchActionIndices = new List<int>();

		private bool CreateBffFile(List<Action> InActions, string BffFilePath, List<Action> PreBuildLocalExecutorActions, List<Action> PostBuildLocalExecutorActions)
		{
			List<Action> Actions = SortActions(InActions);

			try
			{
				bffOutputFileStream = new FileStream(BffFilePath, FileMode.Create, FileAccess.Write);

				WriteEnvironmentSetup(); //Compiler, environment variables and base paths

				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];

					AddText(string.Format("// \"{0}\" {1}\n", Action.CommandPath, Action.CommandArguments));
					List<string> ProducedItems = Action.ProducedItems.ConvertAll(x => string.Format("'{0}'", x.Name));
					AddText(string.Format("//\t=> Producing({0}): \"{1}\"\n", ProducedItems.Count, string.Join("", ProducedItems.ToArray())));

					// Resolve dependencies
					List<int> DependencyIndices = new List<int>();
					foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
					{
						if (PrerequisiteAction != null)
						{
							int ProducingActionIndex = Actions.IndexOf(PrerequisiteAction);
							if (ProducingActionIndex >= 0)
							{
								DependencyIndices.Add(ProducingActionIndex);
							}
						}
					}
					
					switch (Action.ActionType)
					{
						case ActionType.Compile:
							if (AddCompileAction(Action, ActionIndex, DependencyIndices))
							{
								FastBuildActionIndices.Add(ActionIndex);
							}
							break;
						case ActionType.Link:
							if (AddLinkAction(Actions, ActionIndex, DependencyIndices))
							{
								FastBuildActionIndices.Add(ActionIndex);
							}
							break;
						case ActionType.BuildProject: PreBuildLocalExecutorActions.Add(Action); break;
						default: PostBuildLocalExecutorActions.Add(Action); break;
					}
				}

				AddText("\n\n");
				AddText("Alias( 'all' ) \n{\n");
				AddText("\t.Targets = { \n");
				int numFastBuildActions = FastBuildActionIndices.Count;
				for (int Index = 0; Index < numFastBuildActions; Index++)
				{
					int ActionIndex = FastBuildActionIndices[Index];
					AddText(string.Format("\t\t'Action_{0}'{1}", ActionIndex, Index < (numFastBuildActions - 1) ? "," : ""));

					if (FastBuildTouchActionIndices.Contains(ActionIndex))
					{
						AddText(string.Format("\n\t\t{0}'Action_{1}_Touch'{2}", Index < (numFastBuildActions - 1) ? "" : ",", ActionIndex, Index < (numFastBuildActions - 1) ? "," : ""));
					}

					AddText(string.Format("{0}", Index < (numFastBuildActions - 1) ? "\n" : "\n\t}\n"));
				}

				if (numFastBuildActions == 0)
				{
					AddText("\t}\n");
				}

				AddText("}\n"); // end of .Targets

				bffOutputFileStream.Close();
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception while creating bff file: " + e.ToString());
				return false;
			}

			return true;
		}

		private bool ExecuteBffFile(string BffFilePath)
		{
    		string cacheArgument = "";

			if (bEnableCaching)
			{
				switch (CacheMode)
				{
					case eCacheMode.ReadOnly:
						cacheArgument = "-cacheread";
						break;
					case eCacheMode.WriteOnly:
						cacheArgument = "-cachewrite";
						break;
					case eCacheMode.ReadWrite:
						cacheArgument = "-cache";
						break;
				}
			}

    		string distArgument = bEnableDistribution ? "-dist" : "";

			string verboseArgument = bVerboseMode ? "-verbose" : "";

			string distVerboseArgument = bDistVerboseMode ? "-distverbose" : "";

			string fastCancelArgument = bFastCancel ? "-fastcancel" : "";

			//Interesting flags for FASTBuild: -nostoponerror, -verbose, -monitor (if FASTBuild Monitor Visual Studio Extension is installed!)
			// Yassine: The -clean is to bypass the FastBuild internal dependencies checks (cached in the fdb) as it could create some conflicts with UBT.
			//			Basically we want FB to stupidly compile what UBT tells it to.
			string FBCommandLine = string.Format("-monitor -summary {0} {1} {2} {3} -ide -clean {4} -config \"{5}\"", distArgument, cacheArgument, verboseArgument, distVerboseArgument, fastCancelArgument, BffFilePath);

			ProcessStartInfo FBStartInfo = new ProcessStartInfo(string.IsNullOrEmpty(FBuildExePathOverride) ? "fbuild" : FBuildExePathOverride, FBCommandLine);

			FBStartInfo.UseShellExecute = false;
			FBStartInfo.WorkingDirectory = Path.Combine(UnrealBuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory()), "Source");

			try
			{
				Process FBProcess = new Process();
				FBProcess.StartInfo = FBStartInfo;

				FBStartInfo.RedirectStandardError = true;
				FBStartInfo.RedirectStandardOutput = true;
				FBProcess.EnableRaisingEvents = true;

				DataReceivedEventHandler OutputEventHandler = (Sender, Args) =>
				{
					if (Args.Data != null)
						Console.WriteLine(Args.Data);
				};

				FBProcess.OutputDataReceived += OutputEventHandler;
				FBProcess.ErrorDataReceived += OutputEventHandler;

				FBProcess.Start();

				FBProcess.BeginOutputReadLine();
				FBProcess.BeginErrorReadLine();

				FBProcess.WaitForExit();
				return FBProcess.ExitCode == 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception launching fbuild process. Is it in your path?" + e.ToString());
				return false;
			}
		}
	}
}
