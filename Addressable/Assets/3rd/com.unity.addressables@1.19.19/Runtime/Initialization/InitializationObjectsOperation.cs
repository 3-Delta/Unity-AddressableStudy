using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;

namespace UnityEngine.ResourceManagement.AsyncOperations
{
    internal class InitObjectsOperation : AsyncOperationBase<bool>
    {
        private AsyncOperationHandle<ResourceManagerRuntimeData> m_RtdOp;
        private AddressablesImpl impl;
        private AsyncOperationHandle<IList<AsyncOperationHandle>> m_DepOp;

        public void Init(AsyncOperationHandle<ResourceManagerRuntimeData> rtdOp, AddressablesImpl addressables)
        {
            m_RtdOp = rtdOp;
            this.impl = addressables;
            this.impl.ResourceManager.RegisterForCallbacks();
        }

        protected override string DebugName
        {
            get { return "InitializationObjectsOperation"; }
        }

        internal bool LogRuntimeWarnings(string pathToBuildLogs)
        {
            if (!File.Exists(pathToBuildLogs))
                return false;

            PackedPlayModeBuildLogs runtimeBuildLogs = JsonUtility.FromJson<PackedPlayModeBuildLogs>(File.ReadAllText(pathToBuildLogs));
            bool messageLogged = false;
            foreach (var log in runtimeBuildLogs.RuntimeBuildLogs)
            {
                messageLogged = true;
                switch (log.Type)
                {
                    case LogType.Warning:
                        Addressables.LogWarning(log.Message);
                        break;
                    case LogType.Error:
                        Addressables.LogError(log.Message);
                        break;
                    case LogType.Log:
                        Addressables.Log(log.Message);
                        break;
                }
            }

            return messageLogged;
        }

        /// <inheritdoc />
        protected override bool IsComplete()
        {
            if (IsDone)
                return true;
            if (m_RtdOp.IsValid() && !m_RtdOp.IsDone)
                m_RtdOp.WaitForCompletion();

            m_RM?.Update(Time.unscaledDeltaTime);

            if (!HasExecuted)
                InvokeExecute();

            if (m_DepOp.IsValid() && !m_DepOp.IsDone)
                m_DepOp.WaitForCompletion();
            m_RM?.Update(Time.unscaledDeltaTime);

            return IsDone;
        }

        protected override void WhenDependentCompleted()
        {
            var rtd = m_RtdOp.Result;
            if (rtd == null)
            {
                Addressables.LogError("RuntimeData is null.  Please ensure you have built the correct Player Content.");
                Complete(true, true, "");
                return;
            }

            string buildLogsPath = this.impl.ResolveInternalId(PlayerPrefs.GetString(Addressables.kAddressablesRuntimeBuildLogPath));
            if (LogRuntimeWarnings(buildLogsPath))
                File.Delete(buildLogsPath);

            List<AsyncOperationHandle> initOperations = new List<AsyncOperationHandle>();
            foreach (var i in rtd.InitializationObjects)
            {
                if (i.ObjectType.Value == null)
                {
                    Addressables.LogFormat("Invalid initialization object type {0}.", i.ObjectType);
                    continue;
                }

                try
                {
                    var o = i.GetAsyncInitHandle(this.impl.ResourceManager);
                    initOperations.Add(o);
                    Addressables.LogFormat("Initialization object {0} created instance {1}.", i, o);
                }
                catch (Exception ex)
                {
                    Addressables.LogErrorFormat("Exception thrown during initialization of object {0}: {1}", i,
                        ex.ToString());
                }
            }

            m_DepOp = this.impl.ResourceManager.CreateGenericGroupOperation(initOperations, true);
            m_DepOp.Completed += (obj) =>
            {
                bool success = obj.Status == AsyncOperationStatus.Succeeded;
                Complete(true, success, success ? "" : $"{obj.DebugName}, status={obj.Status}, result={obj.Result} failed initialization.");
                this.impl.Release(m_DepOp);
            };
        }
    }
}
