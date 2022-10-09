using System.Collections.Generic;
using System.Linq;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace UnityEngine.AddressableAssets
{
    class UpdateCatalogsOperation : AsyncOperationBase<List<IResourceLocator>>
    {
        AddressablesImpl impl;
        List<AddressablesImpl.ResourceLocatorInfo> m_LocatorInfos;
        AsyncOperationHandle<IList<AsyncOperationHandle>> m_DepOp;
        AsyncOperationHandle<bool> m_CleanCacheOp;
        bool m_AutoCleanBundleCache = false;

        public UpdateCatalogsOperation(AddressablesImpl aa)
        {
            this.impl = aa;
        }

        public AsyncOperationHandle<List<IResourceLocator>> Start(IEnumerable<string> catalogIds, bool autoCleanBundleCache)
        {
            m_LocatorInfos = new List<AddressablesImpl.ResourceLocatorInfo>();
            var locations = new List<IResourceLocation>();
            foreach (var c in catalogIds)
            {
                if (c == null)
                    continue;
                var loc = this.impl.GetLocatorInfo(c);
                locations.Add(loc.CatalogLocation);
                m_LocatorInfos.Add(loc);
            }
            if (locations.Count == 0)
                return this.impl.ResourceManager.CreateCompletedOperation(default(List<IResourceLocator>), "Content update not available.");

            ContentCatalogProvider ccp = this.impl.ResourceManager.ResourceProviders
                .FirstOrDefault(rp => rp.GetType() == typeof(ContentCatalogProvider)) as ContentCatalogProvider;
            if (ccp != null)
                ccp.DisableCatalogUpdateOnStart = false;

            m_DepOp = this.impl.ResourceManager.CreateGroupOperation<object>(locations);
            m_AutoCleanBundleCache = autoCleanBundleCache;
            return this.impl.ResourceManager.StartOperation(this, m_DepOp);
        }

        /// <inheritdoc />
        protected override bool IsComplete()
        {
            if (IsDone)
                return true;
            if (m_DepOp.IsValid() && !m_DepOp.IsDone)
                m_DepOp.WaitForCompletion();

            m_RM?.Update(Time.unscaledDeltaTime);
            if (!HasExecuted)
                InvokeExecute();

            if (m_CleanCacheOp.IsValid() && !m_CleanCacheOp.IsDone)
                m_CleanCacheOp.WaitForCompletion();

            this.impl.ResourceManager.Update(Time.unscaledDeltaTime);
            return IsDone;
        }

        protected override void WhenRefCountReachZero()
        {
            this.impl.Release(m_DepOp);
        }

        /// <inheritdoc />
        public override void GetDependencies(List<AsyncOperationHandle> dependencies)
        {
            dependencies.Add(m_DepOp);
        }

        protected override void WhenDependentCompleted()
        {
            var catalogs = new List<IResourceLocator>(m_DepOp.Result.Count);
            for (int i = 0; i < m_DepOp.Result.Count; i++)
            {
                var locator = m_DepOp.Result[i].Result as IResourceLocator;
                string localHash = null;
                IResourceLocation remoteLocation = null;
                if (locator == null)
                {
                    var catData = m_DepOp.Result[i].Result as ContentCatalogData;
                    locator = catData.CreateCustomLocator(catData.location.PrimaryKey);
                    localHash = catData.localHash;
                    remoteLocation = catData.location;
                }

                m_LocatorInfos[i].UpdateContent(locator, localHash, remoteLocation);
                catalogs.Add(m_LocatorInfos[i].Locator);
            }

            if (!m_AutoCleanBundleCache)
                Complete(catalogs, true, null);
            else
            {
                m_CleanCacheOp = this.impl.CleanBundleCache(m_DepOp, false);
                OnCleanCacheCompleted(m_CleanCacheOp, catalogs);
            }
        }

        void OnCleanCacheCompleted(AsyncOperationHandle<bool> handle, List<IResourceLocator> catalogs)
        {
            handle.Completed += (obj) =>
            {
                bool success = obj.Status == AsyncOperationStatus.Succeeded;
                Complete(catalogs, success, success ? null : $"{obj.DebugName}, status={obj.Status}, result={obj.Result} catalogs updated, but failed to clean bundle cache.");
            };
        }
    }
}
