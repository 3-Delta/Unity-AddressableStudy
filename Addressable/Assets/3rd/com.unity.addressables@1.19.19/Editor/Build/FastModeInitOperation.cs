using System.Collections.Generic;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.AddressableAssets.Utility;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.ResourceManagement.Util;

namespace UnityEditor.AddressableAssets.Settings
{
    internal class FastModeInitOperation : AsyncOperationBase<IResourceLocator>
    {
        AddressablesImpl impl;
        AddressableAssetSettings m_settings;
        internal ResourceManagerDiagnostics m_Diagnostics;
        AsyncOperationHandle<IList<AsyncOperationHandle>> groupOp;
        public FastModeInitOperation(AddressablesImpl impl, AddressableAssetSettings settings)
        {
            this.impl = impl;
            m_settings = settings;
            this.impl.ResourceManager.RegisterForCallbacks();
            m_Diagnostics = new ResourceManagerDiagnostics(this.impl.ResourceManager);
        }

        static T GetBuilderOfType<T>(AddressableAssetSettings settings) where T : class, IDataBuilder
        {
            foreach (var db in settings.DataBuilders)
            {
                var b = db;
                if (b.GetType() == typeof(T))
                    return b as T;
            }
            return null;
        }

        ///<inheritdoc />
        protected override bool IsComplete()
        {
            if (IsDone)
                return true;

            m_RM?.Update(Time.unscaledDeltaTime);
            if(!HasExecuted)
                InvokeExecute();
            return true;
        }

        protected override void WhenDependentCompleted()
        {
            var db = GetBuilderOfType<BuildScriptFastMode>(m_settings);
            if (db == null)
                UnityEngine.Debug.Log($"Unable to find {nameof(BuildScriptFastMode)} builder in settings assets. Using default Instance and Scene Providers.");

            var locator = new AddressableAssetSettingsLocator(m_settings);
            this.impl.AddResourceLocator(locator);
            this.impl.AddResourceLocator(new DynamicResourceLocator(this.impl));
            this.impl.ResourceManager.postProfilerEvents = ProjectConfigData.PostProfilerEvents;
            if (!this.impl.ResourceManager.postProfilerEvents)
            {
                m_Diagnostics.Dispose();
                m_Diagnostics = null;
                this.impl.ResourceManager.ClearDiagnosticCallbacks();
            }

            if (!m_settings.buildSettings.LogResourceManagerExceptions)
                ResourceManager.ExceptionHandler = null;

            //NOTE: for some reason, the data builders can get lost from the settings asset during a domain reload - this only happens in tests and custom instance and scene providers are not needed
            this.impl.InstanceProvider = db == null ? new InstanceProvider() : ObjectInitData.CreateSerializedInitData(db.instanceProviderType.Value).CreateInstance<IInstanceProvider>();
            this.impl.SceneProvider = db == null ? new SceneProvider() : ObjectInitData.CreateSerializedInitData(db.sceneProviderType.Value).CreateInstance<ISceneProvider>();
            this.impl.ResourceManager.ResourceProviders.Add(new AssetDatabaseProvider());
            this.impl.ResourceManager.ResourceProviders.Add(new TextDataProvider());
            this.impl.ResourceManager.ResourceProviders.Add(new JsonAssetProvider());
            this.impl.ResourceManager.ResourceProviders.Add(new LegacyResourcesProvider());
            this.impl.ResourceManager.ResourceProviders.Add(new AtlasSpriteProvider());
            this.impl.ResourceManager.ResourceProviders.Add(new ContentCatalogProvider(this.impl.ResourceManager));
            WebRequestQueue.SetMaxConcurrentRequests(m_settings.MaxConcurrentWebRequests);
            this.impl.CatalogRequestsTimeout = m_settings.CatalogRequestsTimeout;

            if (m_settings.InitializationObjects.Count == 0)
            {
                Complete(locator, true, null);
            }
            else
            {
                List<AsyncOperationHandle> initOperations = new List<AsyncOperationHandle>();
                foreach (var io in m_settings.InitializationObjects)
                {   // 执行AddressableSettings文件中配置的初始化项， 因为是CacheInitialization类型的，所以会继续执行CacheInitialization.cs.Start
                    if (io is IObjectInitializationDataProvider provider)
                    {
                        var ioData = provider.CreateObjectInitData();
                        var h = ioData.GetAsyncInitHandle(this.impl.ResourceManager);
                        initOperations.Add(h);
                    }
                }

                groupOp = this.impl.ResourceManager.CreateGenericGroupOperation(initOperations, true);
                groupOp.Completed += op =>
                {
                    bool success = op.Status == AsyncOperationStatus.Succeeded;
                    Complete(locator, success, success ? "" : $"{op.DebugName}, status={op.Status}, result={op.Result} failed initialization.");
                    this.impl.Release(op);
                };
            }
        }
    }
}
