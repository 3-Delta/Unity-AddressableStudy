using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor.AddressableAssets.Build.BuildPipelineTasks;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Tasks;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.Build.Pipeline;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.ResourceManagement.Util;
using static UnityEditor.AddressableAssets.Build.ContentUpdateScript;

namespace UnityEditor.AddressableAssets.Build.DataBuilders
{
    using Debug = UnityEngine.Debug;

    /// <summary>
    /// Build scripts used for player builds and running with bundles in the editor.
    /// </summary>
    [CreateAssetMenu(fileName = "BuildScriptPacked.asset", menuName = "Addressables/Content Builders/Default Build Script")]
    public class BuildScriptPackedMode : BuildScriptBase
    {
        /// <inheritdoc />
        public override string Name
        {
            get
            {
                return "Default Build Script(sbp)";
            }
        }

        internal List<ObjectInitData> m_ResourceProviderData;
        List<AssetBundleBuild> m_Allabbs;
        List<string> m_OutputUnHashABNames;
        HashSet<string> m_CreatedProviderIds;
        UnityEditor.Build.Pipeline.Utilities.LinkXmlGenerator m_Linker;
        Dictionary<string, string> m_BundleToInternalId = new Dictionary<string, string>();
        private string m_CatalogBuildPath;

        internal List<ObjectInitData> ResourceProviderData => m_ResourceProviderData.ToList();

        /// <inheritdoc />
        public override bool CanBuildData<T>()
        {
            return typeof(T).IsAssignableFrom(typeof(AddressablesPlayerBuildResult));
        }

        /// <inheritdoc />
        protected override TResult BuildDataImplementation<TResult>(AddressablesDataBuilderInput builderInput)
        {
            TResult result = default(TResult);

            var timer = new Stopwatch();
            timer.Start();
            InitializeBuildContext(builderInput, out AddressableAssetsBuildContext aaContext);

            using (m_Log.ScopedStep(LogLevel.Info, "ProcessAllGroups"))
            {
                // 设置group和abb的关系
                var errorString = ProcessAllGroups(aaContext);
                if (!string.IsNullOrEmpty(errorString))
                    result = AddressableAssetBuildResult.CreateResult<TResult>(null, 0, errorString);
            }

            if (result == null)
            {
                result = DoBuild<TResult>(builderInput, aaContext);
            }

            if (result != null)
                result.Duration = timer.Elapsed.TotalSeconds;

            return result;
        }

        internal void InitializeBuildContext(AddressablesDataBuilderInput builderInput, out AddressableAssetsBuildContext aaContext)
        {
            var aaSettings = builderInput.AddressableSettings;

            this.m_Allabbs = new List<AssetBundleBuild>();
            this.m_OutputUnHashABNames = new List<string>();
            var bundleToAssetGroup = new Dictionary<string, string>();
            var runtimeData = new ResourceManagerRuntimeData
            {
                CertificateHandlerType = aaSettings.CertificateHandlerType,
                BuildTarget = builderInput.Target.ToString(),
                ProfileEvents = builderInput.ProfilerEventsEnabled,
                LogResourceManagerExceptions = aaSettings.buildSettings.LogResourceManagerExceptions,
                DisableCatalogUpdateOnStartup = aaSettings.DisableCatalogUpdateOnStartup,
                IsLocalCatalogInBundle = aaSettings.BundleLocalCatalog,
#if UNITY_2019_3_OR_NEWER
                AddressablesVersion = "1.19.19",
#endif
                MaxConcurrentWebRequests = aaSettings.MaxConcurrentWebRequests,
                CatalogRequestsTimeout = aaSettings.CatalogRequestsTimeout
            };
            m_Linker = UnityEditor.Build.Pipeline.Utilities.LinkXmlGenerator.CreateDefault();
            m_Linker.AddAssemblies(new[] { typeof(Addressables).Assembly, typeof(UnityEngine.ResourceManagement.ResourceManager).Assembly });
            m_Linker.AddTypes(runtimeData.CertificateHandlerType);

            m_ResourceProviderData = new List<ObjectInitData>();
            aaContext = new AddressableAssetsBuildContext
            {
                Settings = aaSettings,
                runtimeData = runtimeData,
                bundleToAssetGroup = bundleToAssetGroup,
                locations = new List<ContentCatalogDataEntry>(),
                providerTypes = new HashSet<Type>(),
                assetEntries = new List<AddressableAssetEntry>()
            };

            m_CreatedProviderIds = new HashSet<string>();
        }

        struct SBPSettingsOverwriterScope : IDisposable
        {
            bool m_PrevSlimResults;
            public SBPSettingsOverwriterScope(bool forceFullWriteResults)
            {
                m_PrevSlimResults = ScriptableBuildPipeline.slimWriteResults;
                if (forceFullWriteResults)
                    ScriptableBuildPipeline.slimWriteResults = false;
            }

            public void Dispose()
            {
                ScriptableBuildPipeline.slimWriteResults = m_PrevSlimResults;
            }
        }

        internal static string GetBuiltInShaderBundleNamePrefix(AddressableAssetsBuildContext aaContext)
        {
            return GetBuiltInShaderBundleNamePrefix(aaContext.Settings);
        }
        
        internal static string GetBuiltInShaderBundleNamePrefix(AddressableAssetSettings settings)
        {
            string value = "";
            switch (settings.ShaderBundleNaming)
            {
                case ShaderBundleNaming.DefaultGroupGuid:
                    value = settings.DefaultGroup.Guid;
                    break;
                case ShaderBundleNaming.ProjectName:
                    value = Hash128.Compute(GetProjectName()).ToString();
                    break;
                case ShaderBundleNaming.Custom:
                    value = settings.ShaderBundleCustomNaming;
                    break;
            }

            return value;
        }

        void AddBundleProvider(BundledAssetGroupSchema schema)
        {
            var bundleProviderId = schema.GetBundleCachedProviderId();

            if (!m_CreatedProviderIds.Contains(bundleProviderId))
            {
                m_CreatedProviderIds.Add(bundleProviderId);
                var bundleProviderType = schema.AssetBundleProviderType.Value;
                var bundleProviderData = ObjectInitData.CreateSerializedInitData(bundleProviderType, bundleProviderId);
                m_ResourceProviderData.Add(bundleProviderData);
            }
        }

        internal static string GetMonoScriptBundleNamePrefix(AddressableAssetsBuildContext aaContext)
        {
            return GetMonoScriptBundleNamePrefix(aaContext.Settings);
        }

        internal static string GetMonoScriptBundleNamePrefix(AddressableAssetSettings settings)
        {
            string value = null;
            switch (settings.MonoScriptBundleNaming)
            {
                case MonoScriptBundleNaming.ProjectName:
                    value = Hash128.Compute(GetProjectName()).ToString();
                    break;
                case MonoScriptBundleNaming.DefaultGroupGuid:
                    value = settings.DefaultGroup.Guid;
                    break;
                case MonoScriptBundleNaming.Custom:
                    value = settings.MonoScriptBundleCustomNaming;
                    break;
            }

            return value;
        }

        /// <summary>
        /// The method that does the actual building after all the groups have been processed.
        /// </summary>
        /// <param name="builderInput">The generic builderInput of the</param>
        /// <param name="aaContext"></param>
        /// <typeparam name="TResult"></typeparam>
        /// <returns></returns>
        protected virtual TResult DoBuild<TResult>(AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext) where TResult : IDataBuilderResult
        {
            ExtractDataTask extractData = new ExtractDataTask();
            List<CachedAssetState> carryOverCachedState = new List<CachedAssetState>();
            var tempPath = Path.GetDirectoryName(Application.dataPath) + "/" + Addressables.LibraryPath + PlatformMappingService.GetPlatformPathSubFolder() + "/LastAA_ContentBuild_State.bin";

            var playerBuildVersion = builderInput.PlayerVersion;
            if (this.m_Allabbs.Count > 0)
            {
                if (!BuildUtility.CheckModifiedScenesAndAskToSave())
                    return AddressableAssetBuildResult.CreateResult<TResult>(null, 0, "Unsaved scenes");

                var buildTarget = builderInput.Target;
                var buildTargetGroup = builderInput.TargetGroup;

                var buildParams = new AddressableAssetsBundleBuildParameters(
                    aaContext.Settings,
                    // 此时经过ProcessAllGroups的收集，aaContect只有assetEntries和bundleToAssetGroup有值
                    // bundleToAssetGroup是hashabName:group.guid, 函数HandleDuplicateBundleNames中处理
                    aaContext.bundleToAssetGroup,
                    buildTarget,
                    buildTargetGroup,
                    // Temp/com.unity.addressables/AssetBundles
                    aaContext.Settings.buildSettings.bundleBuildPath);

                var builtinShaderBundleName = GetBuiltInShaderBundleNamePrefix(aaContext) + "_unitybuiltinshaders.bundle";

                var schema = aaContext.Settings.DefaultGroup.GetSchema<BundledAssetGroupSchema>();
                AddBundleProvider(schema);

                string monoScriptBundleName = GetMonoScriptBundleNamePrefix(aaContext);
                if (!string.IsNullOrEmpty(monoScriptBundleName))
                    monoScriptBundleName += "_monoscripts.bundle";
                
                // 获取group和abb的关系之后，执行这些task,在temp目录下生成ab
                var buildTasks = RuntimeDataBuildTasks(builtinShaderBundleName, monoScriptBundleName);
                buildTasks.Add(extractData);

                IBundleBuildResults results;
                using (m_Log.ScopedStep(LogLevel.Info, "ContentPipeline.BuildAssetBundles"))
                using (new SBPSettingsOverwriterScope(ProjectConfigData.GenerateBuildLayout)) // build layout generation requires full SBP write results
                {
                    // 构建ab, 从buildcache到temp目录，后面会将这里的ab拷贝到libray目录下
                    var exitCode = ContentPipeline.BuildAssetBundles(buildParams, new BundleBuildContent(this.m_Allabbs), out results, buildTasks, aaContext, m_Log);

                    if (exitCode < ReturnCode.Success)
                        return AddressableAssetBuildResult.CreateResult<TResult>(null, 0, "SBP Error" + exitCode);
                }

                var groups = aaContext.Settings.groups.Where(g => g != null);

                var bundleRenameMap = new Dictionary<string, string>();
                var postCatalogUpdateCallbacks = new List<Action>();
                using (m_Log.ScopedStep(LogLevel.Info, "PostProcessBundles"))
                using (var progressTracker = new UnityEditor.Build.Pipeline.Utilities.ProgressTracker())
                {
                    progressTracker.UpdateTask("Post Processing AssetBundles");

                    Dictionary<string, ContentCatalogDataEntry> primaryKeyToCatalogEntry = new Dictionary<string, ContentCatalogDataEntry>();
                    foreach (var loc in aaContext.locations)
                        if (loc != null && loc.Keys[0] != null && loc.Keys[0] is string && !primaryKeyToCatalogEntry.ContainsKey((string)loc.Keys[0]))
                            primaryKeyToCatalogEntry[(string)loc.Keys[0]] = loc;

                    foreach (var assetGroup in groups)
                    {
                        // 每个group对应有几个hashBundle
                        // m_OutputABNames是正常abbname的集合，不是hashbundlename的集合
                        // abb.assetbundlename是hash过的
                        if (aaContext.assetGroupToBundles.TryGetValue(assetGroup, out List<string> hashBundles))
                        {
                            using (m_Log.ScopedStep(LogLevel.Info, assetGroup.name))
                            {
                                List<string> outputBundles = new List<string>();
                                for (int i = 0; i < hashBundles.Count; ++i)
                                {
                                    // 此时abb.assetBundleName也是hash
                                    // b != -1, 表示不是内置ab,是普通ab
                                    var b = this.m_Allabbs.FindIndex(abb =>
                                        hashBundles[i].StartsWith(abb.assetBundleName));
                                    outputBundles.Add(b >= 0 ? this.m_OutputUnHashABNames[b] : hashBundles[i]);
                                }

                                PostProcessBundles(assetGroup, hashBundles, outputBundles, results,
                                    aaContext.runtimeData, aaContext.locations, builderInput.Registry,
                                    primaryKeyToCatalogEntry, bundleRenameMap, postCatalogUpdateCallbacks);
                            }
                        }
                    }
                }

                using (m_Log.ScopedStep(LogLevel.Info, "Process Catalog Entries"))
                {
                    ProcessCatalogEntriesForBuild(aaContext, groups, builderInput, extractData.WriteData,
                        carryOverCachedState, m_BundleToInternalId);
                    foreach (var postUpdateCatalogCallback in postCatalogUpdateCallbacks)
                        postUpdateCatalogCallback.Invoke();

                    foreach (var r in results.WriteResults)
                    {
                        var resultValue = r.Value;
                        m_Linker.AddTypes(resultValue.includedTypes);
#if UNITY_2021_1_OR_NEWER
                        m_Linker.AddSerializedClass(resultValue.includedSerializeReferenceFQN);
#else
                    if (resultValue.GetType().GetProperty("includedSerializeReferenceFQN") != null)
                        m_Linker.AddSerializedClass(resultValue.GetType().GetProperty("includedSerializeReferenceFQN").GetValue(resultValue) as System.Collections.Generic.IEnumerable<string>);
#endif
                    }
                }

                using (m_Log.ScopedStep(LogLevel.Info, "Generate Build Layout"))
                {
                    if (ProjectConfigData.GenerateBuildLayout)
                    {
                        using (var progressTracker = new UnityEditor.Build.Pipeline.Utilities.ProgressTracker())
                        {
                            progressTracker.UpdateTask("Generating Build Layout");
                            List<IBuildTask> tasks = new List<IBuildTask>();
                            var buildLayoutTask = new BuildLayoutGenerationTask();
                            buildLayoutTask.m_BundleNameRemap = bundleRenameMap;
                            tasks.Add(buildLayoutTask);
                            BuildTasksRunner.Run(tasks, extractData.m_BuildContext);
                        }
                    }
                }
            }

            ContentCatalogData contentCatalog;
            using (m_Log.ScopedStep(LogLevel.Info, "Generate Catalog"))
            {
                contentCatalog = new ContentCatalogData(ResourceManagerRuntimeData.kCatalogAddress);
                contentCatalog.SetData(aaContext.locations.OrderBy(f => f.InternalId).ToList(), aaContext.Settings.OptimizeCatalogSize);

                contentCatalog.ResourceProviderData.AddRange(m_ResourceProviderData);
                foreach (var t in aaContext.providerTypes)
                    contentCatalog.ResourceProviderData.Add(ObjectInitData.CreateSerializedInitData(t));

                contentCatalog.InstanceProviderData = ObjectInitData.CreateSerializedInitData(instanceProviderType.Value);
                contentCatalog.SceneProviderData = ObjectInitData.CreateSerializedInitData(sceneProviderType.Value);

                //save catalog
                var jsonText = JsonUtility.ToJson(contentCatalog);
                CreateCatalogFiles(jsonText, builderInput, aaContext);
            }

            using (m_Log.ScopedStep(LogLevel.Info, "Generate link"))
            {
                foreach (var pd in contentCatalog.ResourceProviderData)
                {
                    m_Linker.AddTypes(pd.ObjectType.Value);
                    m_Linker.AddTypes(pd.GetRuntimeTypes());
                }

                m_Linker.AddTypes(contentCatalog.InstanceProviderData.ObjectType.Value);
                m_Linker.AddTypes(contentCatalog.InstanceProviderData.GetRuntimeTypes());
                m_Linker.AddTypes(contentCatalog.SceneProviderData.ObjectType.Value);
                m_Linker.AddTypes(contentCatalog.SceneProviderData.GetRuntimeTypes());

                foreach (var io in aaContext.Settings.InitializationObjects)
                {
                    var provider = io as IObjectInitializationDataProvider;
                    if (provider != null)
                    {
                        var id = provider.CreateObjectInitData();
                        aaContext.runtimeData.InitializationObjects.Add(id);
                        m_Linker.AddTypes(id.ObjectType.Value);
                        m_Linker.AddTypes(id.GetRuntimeTypes());
                    }
                }

                m_Linker.AddTypes(typeof(Addressables));
                Directory.CreateDirectory(Addressables.BuildPath + "/AddressablesLink/");
                m_Linker.Save(Addressables.BuildPath + "/AddressablesLink/link.xml");
            }
            
            var settingsPath = Addressables.BuildPath + "/" + builderInput.RuntimeSettingsFilename;
            
            using (m_Log.ScopedStep(LogLevel.Info, "Generate Settings"))
                WriteFile(settingsPath, JsonUtility.ToJson(aaContext.runtimeData), builderInput.Registry);

            if (extractData.BuildCache != null && builderInput.PreviousContentState == null)
            {
                using (m_Log.ScopedStep(LogLevel.Info, "Generate Content Update State"))
                {
                    var remoteCatalogLoadPath = aaContext.Settings.BuildRemoteCatalog
                        ? aaContext.Settings.RemoteCatalogLoadPath.GetValue(aaContext.Settings)
                        : string.Empty;
                    
                    var allEntries = new List<AddressableAssetEntry>();
                    using (m_Log.ScopedStep(LogLevel.Info, "Get Assets"))
                        aaContext.Settings.GetAllAssets(allEntries, false, ContentUpdateScript.GroupFilter);
                    
                    if (ContentUpdateScript.SaveContentState(aaContext.locations, tempPath, allEntries,
                            extractData.DependencyData, playerBuildVersion, remoteCatalogLoadPath,
                            carryOverCachedState))
                    {
                        string contentStatePath = ContentUpdateScript.GetContentStateDataPath(false);
                        try
                        {
                            File.Copy(tempPath, contentStatePath, true);
                            builderInput.Registry.AddFile(contentStatePath);
                            
                            // 额外生成json文件，方便序列化
                            var jsonFile = contentStatePath + ".json";
                            File.Copy(tempPath, jsonFile, true);
                            var cacheData = LoadContentState(jsonFile);
                            File.WriteAllText(jsonFile, cacheData?.ToJson());
                        }
                        catch (UnauthorizedAccessException uae)
                        {
                            if (!AddressableAssetUtility.IsVCAssetOpenForEdit(contentStatePath))
                                Debug.LogErrorFormat("Cannot access the file {0}. It may be locked by version control.",
                                    contentStatePath);
                            else
                                Debug.LogException(uae);
                        }
                        catch (Exception e)
                        {
                            Debug.LogException(e);
                        }
                    }
                }
            }

            return AddressableAssetBuildResult.CreateResult<TResult>(settingsPath, aaContext.locations.Count);
        }

        private static void ProcessCatalogEntriesForBuild(AddressableAssetsBuildContext aaContext,
            IEnumerable<AddressableAssetGroup> validGroups, AddressablesDataBuilderInput builderInput, IBundleWriteData writeData,
            List<CachedAssetState> carryOverCachedState, Dictionary<string, string> bundleToInternalId)
        {
            using (var progressTracker = new UnityEditor.Build.Pipeline.Utilities.ProgressTracker())
            {
                progressTracker.UpdateTask("Post Processing Catalog Entries");
                Dictionary<string, ContentCatalogDataEntry> locationIdToCatalogEntryMap = BuildLocationIdToCatalogEntryMap(aaContext.locations);
                if (builderInput.PreviousContentState != null)
                {
                    // 增量构建
                    ContentUpdateContext contentUpdateContext = new ContentUpdateContext()
                    {
                        BundleToInternalBundleIdMap = bundleToInternalId,
                        // 从builderInput.PreviousContentState获取cacheinfo数据
                        GuidToPreviousAssetStateMap = BuildGuidToCachedAssetStateMap(builderInput.PreviousContentState, aaContext.Settings),
                        IdToCatalogDataEntryMap = locationIdToCatalogEntryMap,
                        WriteData = writeData,
                        ContentState = builderInput.PreviousContentState,
                        Registry = builderInput.Registry,
                        PreviousAssetStateCarryOver = carryOverCachedState
                    };

                    RevertUnchangedAssetsToPreviousAssetState.Run(aaContext, contentUpdateContext);
                }
                else
                {
                    foreach (var assetGroup in validGroups)
                        SetAssetEntriesBundleFileIdToCatalogEntryBundleFileId(assetGroup.entries, bundleToInternalId, writeData, locationIdToCatalogEntryMap);
                }
            }

            bundleToInternalId.Clear();
        }

        private static Dictionary<string, ContentCatalogDataEntry> BuildLocationIdToCatalogEntryMap(List<ContentCatalogDataEntry> locations)
        {
            Dictionary<string, ContentCatalogDataEntry> locationIdToCatalogEntryMap = new Dictionary<string, ContentCatalogDataEntry>();
            foreach (var location in locations)
                locationIdToCatalogEntryMap[location.InternalId] = location;

            return locationIdToCatalogEntryMap;
        }

        private static Dictionary<string, CachedAssetState> BuildGuidToCachedAssetStateMap(AddressablesContentState contentState, AddressableAssetSettings settings)
        {
            Dictionary<string, CachedAssetState> addressableEntryToCachedStateMap = new Dictionary<string, CachedAssetState>();
            foreach (var cachedInfo in contentState.cachedInfos)
                addressableEntryToCachedStateMap[cachedInfo.asset.guid.ToString()] = cachedInfo;

            return addressableEntryToCachedStateMap;
        }

        internal bool CreateCatalogFiles(string jsonText, AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
        {
            if (string.IsNullOrEmpty(jsonText) || builderInput == null || aaContext == null)
            {
                Addressables.LogError("Unable to create content catalog (Null arguments).");
                return false;
            }

            // Path needs to be resolved at runtime.
            string localLoadPath = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/" + builderInput.RuntimeCatalogFilename;
            m_CatalogBuildPath = Path.Combine(Addressables.BuildPath, builderInput.RuntimeCatalogFilename);

            if (aaContext.Settings.BundleLocalCatalog)
            {
                localLoadPath = localLoadPath.Replace(".json", ".bundle");
                m_CatalogBuildPath = m_CatalogBuildPath.Replace(".json", ".bundle");
                var returnCode = CreateCatalogBundle(m_CatalogBuildPath, jsonText, builderInput);
                if (returnCode != ReturnCode.Success || !File.Exists(m_CatalogBuildPath))
                {
                    Addressables.LogError($"An error occured during the creation of the content catalog bundle (return code {returnCode}).");
                    return false;
                }
            }
            else
            {
                WriteFile(m_CatalogBuildPath, jsonText, builderInput.Registry);
            }

            string[] dependencyHashes = null;
            if (aaContext.Settings.BuildRemoteCatalog)
            {
                dependencyHashes = CreateRemoteCatalog(jsonText, aaContext.runtimeData.CatalogLocations, aaContext.Settings, builderInput, new ProviderLoadRequestOptions() {IgnoreFailures = true});
            }

            aaContext.runtimeData.CatalogLocations.Add(new ResourceLocationData(
                new[] { ResourceManagerRuntimeData.kCatalogAddress },
                localLoadPath,
                typeof(ContentCatalogProvider),
                typeof(ContentCatalogData),
                dependencyHashes));

            return true;
        }

        internal static string GetProjectName()
        {
            return new DirectoryInfo(Path.GetDirectoryName(Application.dataPath)).Name;
        }

        internal ReturnCode CreateCatalogBundle(string filepath, string jsonText, AddressablesDataBuilderInput builderInput)
        {
            if (string.IsNullOrEmpty(filepath) || string.IsNullOrEmpty(jsonText) || builderInput == null)
            {
                throw new ArgumentException("Unable to create catalog bundle (null arguments).");
            }

            // A bundle requires an actual asset
            var tempFolderName = "TempCatalogFolder";

            var configFolder = AddressableAssetSettingsDefaultObject.kDefaultConfigFolder;
            if (builderInput.AddressableSettings != null && builderInput.AddressableSettings.IsPersisted)
                configFolder = builderInput.AddressableSettings.ConfigFolder;

            var tempFolderPath = Path.Combine(configFolder, tempFolderName);
            var tempFilePath = Path.Combine(tempFolderPath, Path.GetFileName(filepath).Replace(".bundle", ".json"));
            if (!WriteFile(tempFilePath, jsonText, builderInput.Registry))
            {
                throw new Exception("An error occured during the creation of temporary files needed to bundle the content catalog.");
            }

            AssetDatabase.Refresh();

            var bundleBuildContent = new BundleBuildContent(new[]
            {
                new AssetBundleBuild()
                {
                    assetBundleName = Path.GetFileName(filepath),
                    assetNames = new[] {tempFilePath},
                    addressableNames = new string[0]
                }
            });

            var buildTasks = new List<IBuildTask>
            {
                new CalculateAssetDependencyData(),
                new GenerateBundlePacking(),
                new GenerateBundleCommands(),
                new WriteSerializedFiles(),
                new ArchiveAndCompressBundles()
            };

            var buildParams = new BundleBuildParameters(builderInput.Target, builderInput.TargetGroup, Path.GetDirectoryName(filepath));
            if (builderInput.Target == BuildTarget.WebGL)
                buildParams.BundleCompression = BuildCompression.LZ4Runtime;
            var retCode = ContentPipeline.BuildAssetBundles(buildParams, bundleBuildContent, out IBundleBuildResults result, buildTasks, m_Log);

            if (Directory.Exists(tempFolderPath))
            {
                Directory.Delete(tempFolderPath, true);
                builderInput.Registry.RemoveFile(tempFilePath);
            }

            var tempFolderMetaFile = tempFolderPath + ".meta";
            if (File.Exists(tempFolderMetaFile))
            {
                File.Delete(tempFolderMetaFile);
                builderInput.Registry.RemoveFile(tempFolderMetaFile);
            }

            if (File.Exists(filepath))
            {
                builderInput.Registry.AddFile(filepath);
            }

            return retCode;
        }

        internal static void SetAssetEntriesBundleFileIdToCatalogEntryBundleFileId(ICollection<AddressableAssetEntry> assetEntries, Dictionary<string, string> bundleNameToInternalBundleIdMap,
            IBundleWriteData writeData, Dictionary<string, ContentCatalogDataEntry> locationIdToCatalogEntryMap)
        {
            foreach (var loc in assetEntries)
            {
                AddressableAssetEntry processedEntry = loc;
                if (loc.IsFolder && loc.SubAssets.Count > 0)
                    processedEntry = loc.SubAssets[0];
                GUID guid = new GUID(processedEntry.guid);
                //For every entry in the write data we need to ensure the BundleFileId is set so we can save it correctly in the cached state
                if (writeData.AssetToFiles.TryGetValue(guid, out List<string> files))
                {
                    string file = files[0];
                    string fullBundleName = writeData.FileToBundle[file];
                    string convertedLocation;
                    
                    if (!bundleNameToInternalBundleIdMap.TryGetValue(fullBundleName, out convertedLocation))
                    {
                        Debug.LogException(new Exception($"Unable to find bundleId for key: {fullBundleName}."));
                    }
                    if (locationIdToCatalogEntryMap.TryGetValue(convertedLocation,
                        out ContentCatalogDataEntry catalogEntry))
                    {
                        loc.BundleFileId = catalogEntry.InternalId;

                        //This is where we strip out the temporary hash added to the bundle name for Content Update for the AssetEntry
                        if (loc.parentGroup?.GetSchema<BundledAssetGroupSchema>()?.BundleNaming ==
                            BundledAssetGroupSchema.BundleNamingStyle.NoHash)
                        {
                            loc.BundleFileId = StripHashFromBundleLocation(loc.BundleFileId);
                        }
                    }
                }
            }
        }

        static string StripHashFromBundleLocation(string hashedBundleLocation)
        {
            return hashedBundleLocation.Remove(hashedBundleLocation.LastIndexOf("_")) + ".bundle";
        }

        /// <inheritdoc />
        protected override string ProcessGroup(AddressableAssetGroup assetGroup, AddressableAssetsBuildContext aaContext)
        {
            if (assetGroup == null)
                return string.Empty;

            if (assetGroup.Schemas.Count == 0)
            {
                Addressables.LogWarning($"{assetGroup.Name} does not have any associated AddressableAssetGroupSchemas. " +
                    $"Data from this group will not be included in the build. " +
                    $"If this is unexpected the AddressableGroup may have become corrupted.");
                return string.Empty;
            }

            foreach (var schema in assetGroup.Schemas)
            {
                var errorString = ProcessGroupSchema(schema, assetGroup, aaContext);
                if (!string.IsNullOrEmpty(errorString))
                    return errorString;
            }

            return string.Empty;
        }

        /// <summary>
        /// Called per group per schema to evaluate that schema.  This can be an easy entry point for implementing the
        ///  build aspects surrounding a custom schema.  Note, you should not rely on schemas getting called in a specific
        ///  order.
        /// </summary>
        /// <param name="schema">The schema to process</param>
        /// <param name="assetGroup">The group this schema was pulled from</param>
        /// <param name="aaContext">The general Addressables build builderInput</param>
        /// <returns></returns>
        protected virtual string ProcessGroupSchema(AddressableAssetGroupSchema schema, AddressableAssetGroup assetGroup, AddressableAssetsBuildContext aaContext)
        {
            var playerDataSchema = schema as PlayerDataGroupSchema;
            if (playerDataSchema != null)
                return ProcessPlayerDataSchema(playerDataSchema, assetGroup, aaContext);
            var bundledAssetSchema = schema as BundledAssetGroupSchema;
            if (bundledAssetSchema != null)
                return ProcessBundledAssetSchema(bundledAssetSchema, assetGroup, aaContext);
            return string.Empty;
        }

        internal string ProcessPlayerDataSchema(
            PlayerDataGroupSchema schema,
            AddressableAssetGroup assetGroup,
            AddressableAssetsBuildContext aaContext)
        {
            if (CreateLocationsForPlayerData(schema, assetGroup, aaContext.locations, aaContext.providerTypes))
            {
                if (!m_CreatedProviderIds.Contains(typeof(LegacyResourcesProvider).Name))
                {
                    m_CreatedProviderIds.Add(typeof(LegacyResourcesProvider).Name);
                    m_ResourceProviderData.Add(ObjectInitData.CreateSerializedInitData(typeof(LegacyResourcesProvider)));
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// The processing of the bundled asset schema.  This is where the bundle(s) for a given group are actually setup.
        /// </summary>
        /// <param name="schema">The BundledAssetGroupSchema to process</param>
        /// <param name="assetGroup">The group this schema was pulled from</param>
        /// <param name="aaContext">The general Addressables build builderInput</param>
        /// <returns>The error string, if any.</returns>
        protected virtual string ProcessBundledAssetSchema(
            BundledAssetGroupSchema schema,
            AddressableAssetGroup assetGroup,
            AddressableAssetsBuildContext aaContext)
        {
            if (schema == null || !schema.IncludeInBuild || !assetGroup.entries.Any())
                return string.Empty;

            var errorStr = ErrorCheckBundleSettings(schema, assetGroup, aaContext.Settings);
            if (!string.IsNullOrEmpty(errorStr))
                return errorStr;

            AddBundleProvider(schema);

            var assetProviderId = schema.GetAssetCachedProviderId();
            if (!m_CreatedProviderIds.Contains(assetProviderId))
            {
                m_CreatedProviderIds.Add(assetProviderId);
                var assetProviderType = schema.BundledAssetProviderType.Value;
                var assetProviderData = ObjectInitData.CreateSerializedInitData(assetProviderType, assetProviderId);
                m_ResourceProviderData.Add(assetProviderData);
            }

#if UNITY_2022_1_OR_NEWER
           string loadPath = schema.LoadPath.GetValue(aaContext.Settings);
           if (loadPath.StartsWith("http://") && PlayerSettings.insecureHttpOption == InsecureHttpOption.NotAllowed)
                Addressables.LogWarning($"Addressable group {assetGroup.Name} uses insecure http for its load path.  To allow http connections for UnityWebRequests, change your settings in Edit > Project Settings > Player > Other Settings > Configuration > Allow downloads over HTTP.");
#endif
            if (schema.Compression == BundledAssetGroupSchema.BundleCompressionMode.LZMA && aaContext.runtimeData.BuildTarget == BuildTarget.WebGL.ToString())
                Addressables.LogWarning($"Addressable group {assetGroup.Name} uses LZMA compression, which cannot be decompressed on WebGL. Use LZ4 compression instead.");

            var abbs = new List<AssetBundleBuild>();
            var list = PrepGroupBundlePacking(assetGroup, abbs, schema);
            aaContext.assetEntries.AddRange(list);
            List<string> uniqueNames = HandleDuplicateBundleNames(abbs, aaContext.bundleToAssetGroup, assetGroup.Guid);
            this.m_OutputUnHashABNames.AddRange(uniqueNames);
            m_Allabbs.AddRange(abbs);
            return string.Empty;
        }

        internal static List<string> HandleDuplicateBundleNames(List<AssetBundleBuild> abbs, Dictionary<string, string> bundleToAssetGroup = null, string assetGroupGuid = null)
        {
            var generatedUniqueNames = new List<string>();
            var checkDuplicateNameHash = new HashSet<string>();

            for (int i = 0; i < abbs.Count; i++)
            {
                AssetBundleBuild bundleBuild = abbs[i];
                string assetBundleName = bundleBuild.assetBundleName;
                // group内部的entry重名检测
                if (checkDuplicateNameHash.Contains(assetBundleName))
                {
                    // 同一个group内部的entry有重名，其实就是address一样
                    int count = 1;
                    var newName = assetBundleName;
                    while (checkDuplicateNameHash.Contains(newName) && count < 1000)
                        newName = assetBundleName.Replace(".bundle", string.Format("{0}.bundle", count++));
                    assetBundleName = newName;
                }

                string hashedAssetBundleName = HashingMethods.Calculate(assetBundleName) + ".bundle";
                generatedUniqueNames.Add(assetBundleName);
                checkDuplicateNameHash.Add(assetBundleName);

                bundleBuild.assetBundleName = hashedAssetBundleName;
                abbs[i] = bundleBuild;

                if (bundleToAssetGroup != null)
                    bundleToAssetGroup.Add(hashedAssetBundleName, assetGroupGuid);
            }
            return generatedUniqueNames;
        }

        internal static string ErrorCheckBundleSettings(BundledAssetGroupSchema schema, AddressableAssetGroup assetGroup, AddressableAssetSettings settings)
        {
            var message = string.Empty;

            string buildPath = settings.profileSettings.GetValueById(settings.activeProfileId, schema.BuildPath.Id);
            string loadPath = settings.profileSettings.GetValueById(settings.activeProfileId, schema.LoadPath.Id);

            bool buildLocal = buildPath.Contains("[UnityEngine.AddressableAssets.Addressables.BuildPath]");
            bool loadLocal = loadPath.Contains("{UnityEngine.AddressableAssets.Addressables.RuntimePath}");

            if (buildLocal && !loadLocal)
            {
                message = "BuildPath for group '" + assetGroup.Name + "' is set to the dynamic-lookup version of StreamingAssets, but LoadPath is not. \n";
            }
            else if (!buildLocal && loadLocal)
            {
                message = "LoadPath for group " + assetGroup.Name + " is set to the dynamic-lookup version of StreamingAssets, but BuildPath is not. These paths must both use the dynamic-lookup, or both not use it. \n";
            }

            if (!string.IsNullOrEmpty(message))
            {
                message += "BuildPath: '" + buildPath + "'\n";
                message += "LoadPath: '" + loadPath + "'";
            }
            if (schema.Compression == BundledAssetGroupSchema.BundleCompressionMode.LZMA && (buildLocal || loadLocal))
            {
                Debug.LogWarningFormat("Bundle compression is set to LZMA, but group {0} uses local content.", assetGroup.Name);
            }
            return message;
        }

        internal static string CalculateGroupHash(BundledAssetGroupSchema.BundleInternalIdMode mode, AddressableAssetGroup assetGroup, IEnumerable<AddressableAssetEntry> entries)
        {
            switch (mode)
            {
                case BundledAssetGroupSchema.BundleInternalIdMode.GroupGuid: return assetGroup.Guid;
                case BundledAssetGroupSchema.BundleInternalIdMode.GroupGuidProjectIdHash: return HashingMethods.Calculate(assetGroup.Guid, Application.cloudProjectId).ToString();
                case BundledAssetGroupSchema.BundleInternalIdMode.GroupGuidProjectIdEntriesHash: return HashingMethods.Calculate(assetGroup.Guid, Application.cloudProjectId, new HashSet<string>(entries.Select(e => e.guid))).ToString();
            }
            throw new Exception("Invalid naming mode.");
        }

        /// <summary>
        /// Processes an AddressableAssetGroup and generates AssetBundle input definitions based on the BundlePackingMode.
        /// </summary>
        /// <param name="assetGroup">The AddressableAssetGroup to be processed.</param>
        /// <param name="abbs">The list of bundle definitions fed into the build pipeline AssetBundleBuild</param>
        /// <param name="schema">The BundledAssetGroupSchema of used to process the assetGroup.</param>
        /// <param name="entryFilter">A filter to remove AddressableAssetEntries from being processed in the build.</param>
        /// <returns>The total list of AddressableAssetEntries that were processed.</returns>
        /// 根据group设置abb, abb.abname = (groupHash + "_assets_" + address + ".bundle").tolower();
        public static List<AddressableAssetEntry> PrepGroupBundlePacking(AddressableAssetGroup assetGroup, List<AssetBundleBuild> abbs, BundledAssetGroupSchema schema, Func<AddressableAssetEntry, bool> entryFilter = null)
        {
            var combinedEntries = new List<AddressableAssetEntry>();
            var packingMode = schema.BundleMode;
            var namingMode = schema.InternalBundleIdMode;
            bool ignoreUnsupportedFilesInBuild = assetGroup.Settings.IgnoreUnsupportedFilesInBuild;

            switch (packingMode)
            {
                case BundledAssetGroupSchema.BundlePackingMode.PackTogether:
                {
                    var allEntries = new List<AddressableAssetEntry>();
                    foreach (AddressableAssetEntry a in assetGroup.entries)
                    {
                        if (entryFilter != null && !entryFilter(a))
                            continue;
                        a.GatherAllAssets(allEntries, true, true, false, entryFilter);
                    }
                    combinedEntries.AddRange(allEntries);
                    GenerateBuildInputDefinitions(allEntries, abbs, CalculateGroupHash(namingMode, assetGroup, allEntries), "all", ignoreUnsupportedFilesInBuild);
                } break;
                case BundledAssetGroupSchema.BundlePackingMode.PackSeparately:
                {
                    foreach (AddressableAssetEntry a in assetGroup.entries)
                    {
                        if (entryFilter != null && !entryFilter(a))
                            continue;
                        var allEntries = new List<AddressableAssetEntry>();
                        a.GatherAllAssets(allEntries, true, true, false, entryFilter);
                        combinedEntries.AddRange(allEntries);
                        GenerateBuildInputDefinitions(allEntries, abbs, CalculateGroupHash(namingMode, assetGroup, allEntries), a.address, ignoreUnsupportedFilesInBuild);
                    }
                } break;
                case BundledAssetGroupSchema.BundlePackingMode.PackTogetherByLabel:
                {
                    var labelTable = new Dictionary<string, List<AddressableAssetEntry>>();
                    foreach (AddressableAssetEntry a in assetGroup.entries)
                    {
                        if (entryFilter != null && !entryFilter(a))
                            continue;
                        var sb = new StringBuilder();
                        foreach (var l in a.labels)
                            sb.Append(l);
                        var key = sb.ToString();
                        List<AddressableAssetEntry> entries;
                        if (!labelTable.TryGetValue(key, out entries))
                            labelTable.Add(key, entries = new List<AddressableAssetEntry>());
                        entries.Add(a);
                    }

                    foreach (var entryGroup in labelTable)
                    {
                        var allEntries = new List<AddressableAssetEntry>();
                        foreach (var a in entryGroup.Value)
                        {
                            if (entryFilter != null && !entryFilter(a))
                                continue;
                            a.GatherAllAssets(allEntries, true, true, false, entryFilter);
                        }
                        combinedEntries.AddRange(allEntries);
                        GenerateBuildInputDefinitions(allEntries, abbs, CalculateGroupHash(namingMode, assetGroup, allEntries), entryGroup.Key, ignoreUnsupportedFilesInBuild);
                    }
                } break;
                default:
                    throw new Exception("Unknown Packing Mode");
            }
            return combinedEntries;
        }

        internal static void GenerateBuildInputDefinitions(List<AddressableAssetEntry> allEntries, List<AssetBundleBuild> abbs, string groupHash, string address, bool ignoreUnsupportedFilesInBuild)
        {
            var scenes = new List<AddressableAssetEntry>();
            var assets = new List<AddressableAssetEntry>();
            foreach (var e in allEntries)
            {
                ThrowExceptionIfInvalidFiletypeOrAddress(e, ignoreUnsupportedFilesInBuild);
                if (string.IsNullOrEmpty(e.AssetPath))
                    continue;
                if (e.IsScene)
                    scenes.Add(e);
                else
                    assets.Add(e);
            }
            if (assets.Count > 0)
                abbs.Add(GenerateBuildInputDefinition(assets, groupHash + "_assets_" + address + ".bundle"));
            if (scenes.Count > 0)
                abbs.Add(GenerateBuildInputDefinition(scenes, groupHash + "_scenes_" + address + ".bundle"));
        }

        private static void ThrowExceptionIfInvalidFiletypeOrAddress(AddressableAssetEntry entry, bool ignoreUnsupportedFilesInBuild)
        {
            if (entry.guid.Length > 0 && entry.address.Contains("[") && entry.address.Contains("]"))
                throw new Exception($"Address '{entry.address}' cannot contain '[ ]'.");
            if (entry.MainAssetType == typeof(DefaultAsset) && !AssetDatabase.IsValidFolder(entry.AssetPath))
            {
                if (ignoreUnsupportedFilesInBuild)
                    Debug.LogWarning($"Cannot recognize file type for entry located at '{entry.AssetPath}'. Asset location will be ignored.");
                else
                    throw new Exception($"Cannot recognize file type for entry located at '{entry.AssetPath}'. Asset import failed for using an unsupported file type.");
            }
        }

        internal static AssetBundleBuild GenerateBuildInputDefinition(List<AddressableAssetEntry> assets, string name)
        {
            var assetInternalIds = new HashSet<string>();
            var abb = new AssetBundleBuild();
            // 给ab命名
            abb.assetBundleName = name.ToLower().Replace(" ", "").Replace('\\', '/').Replace("//", "/");
            abb.assetNames = assets.Select(s => s.AssetPath).ToArray();
            abb.addressableNames = assets.Select(s => s.GetAssetLoadPath(true, assetInternalIds)).ToArray();
            return abb;
        }

        static string[] CreateRemoteCatalog(string jsonText, List<ResourceLocationData> locations, AddressableAssetSettings aaSettings, AddressablesDataBuilderInput builderInput, ProviderLoadRequestOptions catalogLoadOptions)
        {
            string[] dependencyHashes = null;

            var contentHash = HashingMethods.Calculate(jsonText).ToString();

            var versionedFileName = aaSettings.profileSettings.EvaluateString(aaSettings.activeProfileId, "/catalog_" + builderInput.PlayerVersion);
            var remoteBuildFolder = aaSettings.RemoteCatalogBuildPath.GetValue(aaSettings);
            var remoteLoadFolder = aaSettings.RemoteCatalogLoadPath.GetValue(aaSettings);

            if (string.IsNullOrEmpty(remoteBuildFolder) ||
                string.IsNullOrEmpty(remoteLoadFolder) ||
                remoteBuildFolder == AddressableAssetProfileSettings.undefinedEntryValue ||
                remoteLoadFolder == AddressableAssetProfileSettings.undefinedEntryValue)
            {
                Addressables.LogWarning("Remote Build and/or Load paths are not set on the main AddressableAssetSettings asset, but 'Build Remote Catalog' is true.  Cannot create remote catalog.  In the inspector for any group, double click the 'Addressable Asset Settings' object to begin inspecting it. '" + remoteBuildFolder + "', '" + remoteLoadFolder + "'");
            }
            else
            {
                var remoteJsonBuildPath = remoteBuildFolder + versionedFileName + ".json";
                var remoteHashBuildPath = remoteBuildFolder + versionedFileName + ".hash";

                WriteFile(remoteJsonBuildPath, jsonText, builderInput.Registry);
                WriteFile(remoteHashBuildPath, contentHash, builderInput.Registry);

                dependencyHashes = new string[((int)ContentCatalogProvider.DependencyHashIndex.Count)];
                dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Remote] = ResourceManagerRuntimeData.kCatalogAddress + "RemoteHash";
                dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Cache] = ResourceManagerRuntimeData.kCatalogAddress + "CacheHash";

                var remoteHashLoadPath = remoteLoadFolder + versionedFileName + ".hash";
                var remoteHashLoadLocation = new ResourceLocationData(
                    new[] {dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Remote]},
                    remoteHashLoadPath,
                    typeof(TextDataProvider), typeof(string));
                remoteHashLoadLocation.Data = catalogLoadOptions.Copy();
                locations.Add(remoteHashLoadLocation);

                var cacheLoadPath = "{UnityEngine.Application.persistentDataPath}/com.unity.addressables" + versionedFileName + ".hash";
                var cacheLoadLocation = new ResourceLocationData(
                    new[] {dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Cache]},
                    cacheLoadPath,
                    typeof(TextDataProvider), typeof(string));
                cacheLoadLocation.Data = catalogLoadOptions.Copy();
                locations.Add(cacheLoadLocation);
            }

            return dependencyHashes;
        }

        // Tests can set this flag to prevent player script compilation. This is the most expensive part of small builds
        // and isn't needed for most tests.
        internal static bool s_SkipCompilePlayerScripts = false;

        // 获取group和abb的关系之后，执行这些task,在temp目录下生成ab
        static IList<IBuildTask> RuntimeDataBuildTasks(string builtinShaderBundleName, string monoScriptBundleName)
        {
            var buildTasks = new List<IBuildTask>();

            // Setup
            buildTasks.Add(new SwitchToBuildPlatform());
            buildTasks.Add(new RebuildSpriteAtlasCache());

            // Player Scripts
            if (!s_SkipCompilePlayerScripts)
                buildTasks.Add(new BuildPlayerScripts());
            buildTasks.Add(new PostScriptsCallback());

            // Dependency
            buildTasks.Add(new CalculateSceneDependencyData());
            buildTasks.Add(new CalculateAssetDependencyData());
            buildTasks.Add(new AddHashToBundleNameTask());
            buildTasks.Add(new StripUnusedSpriteSources());
            buildTasks.Add(new CreateBuiltInShadersBundle(builtinShaderBundleName));
            if (!string.IsNullOrEmpty(monoScriptBundleName))
                buildTasks.Add(new CreateMonoScriptBundle(monoScriptBundleName));
            buildTasks.Add(new PostDependencyCallback());

            // Packing
            buildTasks.Add(new GenerateBundlePacking());
            buildTasks.Add(new UpdateBundleObjectLayout());
            buildTasks.Add(new GenerateBundleCommands());
            buildTasks.Add(new GenerateSubAssetPathMaps());
            buildTasks.Add(new GenerateBundleMaps());
            buildTasks.Add(new PostPackingCallback());

            // Writing
            buildTasks.Add(new WriteSerializedFiles());
            buildTasks.Add(new ArchiveAndCompressBundles());
            buildTasks.Add(new GenerateLocationListsTask());
            buildTasks.Add(new PostWritingCallback());

            return buildTasks;
        }

        static void MoveFileToDestinationWithTimestampIfDifferent(string srcPath, string destPath, IBuildLogger log)
        {
            if (srcPath == destPath)
                return;

            DateTime time = File.GetLastWriteTime(srcPath);
            DateTime destTime = File.Exists(destPath) ? File.GetLastWriteTime(destPath) : new DateTime();

            if (destTime == time)
                return;

            using (log.ScopedStep(LogLevel.Verbose, "Move File", $"{srcPath} -> {destPath}"))
            {
                var directory = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                else if (File.Exists(destPath))
                    File.Delete(destPath); 
                // 将文件从temp -> library下
                File.Move(srcPath, destPath);
            }
        }

        void PostProcessBundles(AddressableAssetGroup assetGroup, List<string> hashBundles, List<string> outputBundles, IBundleBuildResults buildResult, ResourceManagerRuntimeData runtimeData, List<ContentCatalogDataEntry> locations, FileRegistry registry, Dictionary<string, ContentCatalogDataEntry> primaryKeyToCatalogEntry, Dictionary<string, string> bundleRenameMap, List<Action> postCatalogUpdateCallbacks)
        {
            var schema = assetGroup.GetSchema<BundledAssetGroupSchema>();
            if (schema == null)
                return;

            var path = schema.BuildPath.GetValue(assetGroup.Settings);
            if (string.IsNullOrEmpty(path))
                return;

            for (int i = 0; i < hashBundles.Count; ++i)
            {
                if (primaryKeyToCatalogEntry.TryGetValue(hashBundles[i], out ContentCatalogDataEntry dataEntry))
                {
                    // hashbundle:detail 不管是增量，还是全量，先全部构建hashbundle到temp目录下， 然后这个根据hash获取detail
                    var info = buildResult.BundleInfos[hashBundles[i]];
                    var requestOptions = new AssetBundleRequestOptions
                    {
                        Crc = schema.UseAssetBundleCrc ? info.Crc : 0,
                        UseCrcForCachedBundle = schema.UseAssetBundleCrcForCachedBundles,
                        UseUnityWebRequestForLocalBundles = schema.UseUnityWebRequestForLocalBundles,
                        Hash = schema.UseAssetBundleCache ? info.Hash.ToString() : "",
                        ChunkedTransfer = schema.ChunkedTransfer,
                        RedirectLimit = schema.RedirectLimit,
                        RetryCount = schema.RetryCount,
                        Timeout = schema.Timeout,
                        BundleName = Path.GetFileNameWithoutExtension(info.FileName),
                        AssetLoadMode = schema.AssetLoadMode,
                        BundleSize = GetFileSize(info.FileName),
                        ClearOtherCachedVersionsWhenLoaded = schema.AssetBundledCacheClearBehavior == BundledAssetGroupSchema.CacheClearBehavior.ClearWhenWhenNewVersionLoaded
                    };
                    dataEntry.Data = requestOptions;
                    
                    if (assetGroup == assetGroup.Settings.DefaultGroup && info.Dependencies.Length == 0 && !string.IsNullOrEmpty(info.FileName) && (info.FileName.EndsWith("_unitybuiltinshaders.bundle") || info.FileName.EndsWith("_monoscripts.bundle")))
                    {
                        outputBundles[i] = ConstructAssetBundleName(null, schema, info, outputBundles[i]);
                    }
                    else
                    {
                        int extensionLength = Path.GetExtension(outputBundles[i]).Length;
                        string[] deconstructedBundleName = outputBundles[i].Substring(0, outputBundles[i].Length - extensionLength).Split('_');
                        string reconstructedBundleName = string.Join("_", deconstructedBundleName, 1, deconstructedBundleName.Length - 1) + ".bundle";
                        outputBundles[i] = ConstructAssetBundleName(assetGroup, schema, info, reconstructedBundleName);
                    }
                    
                    dataEntry.InternalId = dataEntry.InternalId.Remove(dataEntry.InternalId.Length - hashBundles[i].Length) + outputBundles[i];
                    dataEntry.Keys[0] = outputBundles[i];
                    ReplaceDependencyKeys(hashBundles[i], outputBundles[i], locations);

                    if (!m_BundleToInternalId.ContainsKey(hashBundles[i]))
                        m_BundleToInternalId.Add(hashBundles[i], dataEntry.InternalId);

                    if (dataEntry.InternalId.StartsWith("http:\\"))
                        dataEntry.InternalId = dataEntry.InternalId.Replace("http:\\", "http://").Replace("\\", "/");
                    if (dataEntry.InternalId.StartsWith("https:\\"))
                        dataEntry.InternalId = dataEntry.InternalId.Replace("https:\\", "https://").Replace("\\", "/");
                }
                else
                {
                    Debug.LogWarningFormat("Unable to find ContentCatalogDataEntry for bundle {0}.", outputBundles[i]);
                }

                var targetPath = Path.Combine(path, outputBundles[i]);
                var srcPath = Path.Combine(assetGroup.Settings.buildSettings.bundleBuildPath, hashBundles[i]);
                
                if (assetGroup.GetSchema<BundledAssetGroupSchema>()?.BundleNaming == BundledAssetGroupSchema.BundleNamingStyle.NoHash)
                    outputBundles[i] = StripHashFromBundleLocation(outputBundles[i]);
                
                bundleRenameMap.Add(hashBundles[i], outputBundles[i]);
                // 将文件从temp -> library下
                MoveFileToDestinationWithTimestampIfDifferent(srcPath, targetPath, m_Log);
                AddPostCatalogUpdatesInternal(assetGroup, postCatalogUpdateCallbacks, dataEntry, targetPath, registry);

                registry.AddFile(targetPath);
            }
        }

        internal void AddPostCatalogUpdatesInternal(AddressableAssetGroup assetGroup, List<Action> postCatalogUpdates, ContentCatalogDataEntry dataEntry, string targetBundlePath, FileRegistry registry)
        {
            if (assetGroup.GetSchema<BundledAssetGroupSchema>()?.BundleNaming ==
                BundledAssetGroupSchema.BundleNamingStyle.NoHash)
            {
                postCatalogUpdates.Add(() =>
                {
                    //This is where we strip out the temporary hash for the final bundle location and filename
                    string bundlePathWithoutHash = StripHashFromBundleLocation(targetBundlePath);
                    if (File.Exists(targetBundlePath))
                    {
                        if (File.Exists(bundlePathWithoutHash))
                            File.Delete(bundlePathWithoutHash);
                        string destFolder = Path.GetDirectoryName(bundlePathWithoutHash);
                        if (!string.IsNullOrEmpty(destFolder) && !Directory.Exists(destFolder))
                            Directory.CreateDirectory(destFolder);

                        File.Move(targetBundlePath, bundlePathWithoutHash);
                    }
                    if (registry != null)
                    {
                        if (!registry.ReplaceBundleEntry(targetBundlePath, bundlePathWithoutHash))
                            Debug.LogErrorFormat("Unable to find registered file for bundle {0}.", targetBundlePath);
                    }
                    
                    if (dataEntry != null)
                        if (DataEntryDiffersFromBundleFilename(dataEntry, bundlePathWithoutHash))
                            dataEntry.InternalId = StripHashFromBundleLocation(dataEntry.InternalId);
                });
            }
        }

        // if false, there is no need to remove the hash from dataEntry.InternalId
        bool DataEntryDiffersFromBundleFilename(ContentCatalogDataEntry dataEntry, string bundlePathWithoutHash)
        {
            string dataEntryId = dataEntry.InternalId;
            string dataEntryFilename = Path.GetFileName(dataEntryId);
            string bundleFileName = Path.GetFileName(bundlePathWithoutHash);
            
            return dataEntryFilename != bundleFileName;
        }

        /// <summary>
        /// Creates a name for an asset bundle using the provided information.
        /// </summary>
        /// <param name="assetGroup">The asset group.</param>
        /// <param name="schema">The schema of the group.</param>
        /// <param name="info">The bundle information.</param>
        /// <param name="assetBundleName">The base name of the asset bundle.</param>
        /// <returns>Returns the asset bundle name with the provided information.</returns>
        protected virtual string ConstructAssetBundleName(AddressableAssetGroup assetGroup, BundledAssetGroupSchema schema, BundleDetails info, string assetBundleName)
        {
            if (assetGroup != null)
            {
                string groupName = assetGroup.Name.Replace(" ", "").Replace('\\', '/').Replace("//", "/").ToLower();
                // groupname + abname
                assetBundleName = groupName + "_" + assetBundleName;
            }
            
            // appendhash: groupname + abname + hash
            string bundleNameWithHashing = BuildUtility.GetNameWithHashNaming(schema.BundleNaming, info.Hash.ToString(), assetBundleName);
            //For no hash, we need the hash temporarily for content update purposes.  This will be stripped later on.
            if (schema.BundleNaming == BundledAssetGroupSchema.BundleNamingStyle.NoHash)
            {
                bundleNameWithHashing = bundleNameWithHashing.Replace(".bundle", "_" + info.Hash.ToString() + ".bundle");
            }

            return bundleNameWithHashing;
        }

        static void ReplaceDependencyKeys(string from, string to, List<ContentCatalogDataEntry> locations)
        {
            foreach (ContentCatalogDataEntry location in locations)
            {
                for (int i = 0; i < location.Dependencies.Count; ++i)
                {
                    string s = location.Dependencies[i] as string;
                    if (string.IsNullOrEmpty(s))
                        continue;
                    if (s == from)
                        location.Dependencies[i] = to;
                }
            }
        }

        private static long GetFileSize(string fileName)
        {
            try
            {
                return new FileInfo(fileName).Length;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return 0;
            }
        }

        /// <inheritdoc />
        public override void ClearCachedData()
        {
            if (Directory.Exists(Addressables.BuildPath))
            {
                try
                {
                    var catalogPath = Addressables.BuildPath + "/catalog.json";
                    var settingsPath = Addressables.BuildPath + "/settings.json";
                    DeleteFile(catalogPath);
                    DeleteFile(settingsPath);
                    Directory.Delete(Addressables.BuildPath, true);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <inheritdoc />
        public override bool IsDataBuilt()
        {
            var settingsPath = Addressables.BuildPath + "/settings.json";
            return !String.IsNullOrEmpty(m_CatalogBuildPath) &&
                File.Exists(m_CatalogBuildPath) &&
                File.Exists(settingsPath);
        }
    }
}
