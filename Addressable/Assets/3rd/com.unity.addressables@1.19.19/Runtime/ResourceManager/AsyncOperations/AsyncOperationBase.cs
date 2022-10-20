using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.ResourceManagement.Exceptions;
using UnityEngine.ResourceManagement.Util;

// ReSharper disable DelegateSubtraction

namespace UnityEngine.ResourceManagement.AsyncOperations
{
    internal interface ICachable
    {
        IOperationCacheKey Key { get; set; }
    }

    internal interface IAsyncOperation
    {
        object GetResultAsObject();
        Type ResultType { get; }
        int Version { get; }
        string DebugName { get; }
        
        // 引用计数
        void DecrementReferenceCount();
        void IncrementReferenceCount();
        int ReferenceCount { get; }
        
        float PercentComplete { get; }
        DownloadStatus GetDownloadStatus(HashSet<object> visited);
        AsyncOperationStatus Status { get; }

        Exception OperationException { get; }
        bool IsDone { get; }
        Action<IAsyncOperation> OnDestroy { set; }
        void GetDependencies(List<AsyncOperationHandle> deps);
        bool IsRunning { get; }

        event Action<AsyncOperationHandle> CompletedTypeless;
        event Action<AsyncOperationHandle> Destroyed;

        void InvokeCompletionEvent();
        System.Threading.Tasks.Task<object> Task { get; }
        void Start(ResourceManager rm, AsyncOperationHandle dependency, DelegateList<float> updateCallbacks);

        AsyncOperationHandle Handle { get; }

        void WaitForCompletion();
    }

    /// <summary>
    /// base class for implemented AsyncOperations, implements the needed interfaces and consolidates redundant code
    /// </summary>
    /// <typeparam name="TObject">The type of the operation.</typeparam>
    public abstract class AsyncOperationBase<TObject> : IAsyncOperation
    {
        /// <summary>
        /// This will be called by the resource manager after all dependent operation complete. This method should not be called manually.
        /// A custom operation should override this method and begin work when it is called.
        /// </summary>
        protected abstract void WhenDependentCompleted();

        /// <summary>
        /// This will be called by the resource manager when the reference count of the operation reaches zero. This method should not be called manually.
        /// A custom operation should override this method and release any held resources
        /// </summary>
        protected virtual void WhenRefCountReachZero() {}

        /// <summary>
        /// A custom operation should override this method to return the progress of the operation.
        /// </summary>
        /// <returns>Progress of the operation. Value should be between 0.0f and 1.0f</returns>
        protected virtual float Progress { get { return 0; } }

        /// <summary>
        /// A custom operation should override this method to provide a debug friendly name for the operation.
        /// </summary>
        protected virtual string DebugName { get { return this.ToString(); } }

        /// <summary>
        /// A custom operation should override this method to provide a list of AsyncOperationHandles that it depends on.
        /// </summary>
        /// <param name="dependencies">The list that should be populated with dependent AsyncOperationHandles.</param>
        public virtual void GetDependencies(List<AsyncOperationHandle> dependencies) {}

        /// <summary>
        /// Accessor to Result of the operation.
        /// </summary>
        public TObject Result { get; set; }

        AsyncOperationStatus m_Status;
        
        internal ResourceManager m_RM;
        
        internal int m_Version;
        internal int Version { get { return m_Version; } }

        DelegateList<AsyncOperationHandle> m_DestroyedActionDelList;
        DelegateList<AsyncOperationHandle<TObject>> m_CompletedActionTDelList;
        
        Action<AsyncOperationHandle> m_dependencyCompleteAction;

        internal bool CompletedEventHasListeners => this.m_CompletedActionTDelList != null && this.m_CompletedActionTDelList.Count > 0;
        internal bool DestroyedEventHasListeners => this.m_DestroyedActionDelList != null && this.m_DestroyedActionDelList.Count > 0;

        Action<IAsyncOperation> m_OnDestroyAction;
        internal Action<IAsyncOperation> OnDestroy { set { m_OnDestroyAction = value; } }
        
        int m_referenceCount = 1;
        internal int ReferenceCount { get { return m_referenceCount; } }
        
        protected internal bool HasExecuted = false;

        /// <summary>
        /// True if the current op has begun but hasn't yet reached completion.  False otherwise.
        /// </summary>
        public bool IsRunning { get;  internal set; }
        
        DelegateList<float> m_UpdateDelList;
        Action<float> m_UpdateCallback;

        /// <summary>
        /// Basic constructor for AsyncOperationBase.
        /// </summary>
        protected AsyncOperationBase()
        {
            m_UpdateCallback = UpdateCallback;
            m_dependencyCompleteAction = o => InvokeExecute();
        }

        internal static string ShortenPath(string p, bool keepExtension)
        {
            var slashIndex = p.LastIndexOf('/');
            if (slashIndex > 0)
                p = p.Substring(slashIndex + 1);
            if (!keepExtension)
            {
                slashIndex = p.LastIndexOf('.');
                if (slashIndex > 0)
                    p = p.Substring(0, slashIndex);
            }
            return p;
        }

        internal void IncrementReferenceCount()
        {
            if (m_referenceCount == 0)
                throw new Exception(string.Format("Cannot increment reference count on operation {0} because it has already been destroyed", this));

            m_referenceCount++;
            if (m_RM != null && m_RM.postProfilerEvents)
                m_RM.PostDiagnosticEvent(new ResourceManager.DiagnosticEventContext(new AsyncOperationHandle(this), ResourceManager.DiagnosticEventType.AsyncOperationReferenceCount, m_referenceCount));
        }

        /// <summary>
        /// Synchronously complete the async operation.
        /// </summary>
        public void WaitForCompletion()
        {   // 其实就是当前线程自旋等待，直到目标线程加载完毕
            if (PlatformUtilities.PlatformUsesMultiThreading(Application.platform))
                while (!this.IsComplete()) {}
            else
                throw new Exception($"{Application.platform} does not support synchronous Addressable loading.  Please do not use WaitForCompletion on the {Application.platform} platform.");
        }

        /// <summary>
        /// Used for the implementation of WaitForCompletion in an IAsyncOperation.
        /// </summary>
        /// <returns>True if the operation has completed, otherwise false.</returns>
        protected virtual bool IsComplete() { return true; }

        internal void DecrementReferenceCount()
        {
            if (m_referenceCount <= 0)
                throw new Exception(string.Format("Cannot decrement reference count for operation {0} because it is already 0", this));

            m_referenceCount--;

            if (m_RM != null && m_RM.postProfilerEvents)
                m_RM.PostDiagnosticEvent(new ResourceManager.DiagnosticEventContext(new AsyncOperationHandle(this), ResourceManager.DiagnosticEventType.AsyncOperationReferenceCount, m_referenceCount));

            if (m_referenceCount == 0)
            {
                if (m_RM != null && m_RM.postProfilerEvents)
                    m_RM.PostDiagnosticEvent(new ResourceManager.DiagnosticEventContext(new AsyncOperationHandle(this), ResourceManager.DiagnosticEventType.AsyncOperationDestroy));

                if (this.m_DestroyedActionDelList != null)
                {
                    this.m_DestroyedActionDelList.Invoke(new AsyncOperationHandle<TObject>(this));
                    this.m_DestroyedActionDelList.Clear();
                }

                this.WhenRefCountReachZero();
                Result = default(TObject);
                m_referenceCount = 1;
                m_Status = AsyncOperationStatus.None; // 状态重置
                m_taskCompletionSource = null;
                m_taskCompletionSourceTypeless = null;
                m_Error = null;
                m_Version++; // refCount为0的时候会修改version
                m_RM = null;

                if (m_OnDestroyAction != null)
                {
                    m_OnDestroyAction(this);
                    m_OnDestroyAction = null;
                }
            }
        }

        TaskCompletionSource<TObject> m_taskCompletionSource;
        internal Task<TObject> Task // 有具体泛型类型的Task
        {
            get
            {
                if (m_taskCompletionSource == null)
                {
                    m_taskCompletionSource = new TaskCompletionSource<TObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (IsDone && !CompletedEventHasListeners)
                        m_taskCompletionSource.SetResult(Result);
                }
                return m_taskCompletionSource.Task;
            }
        }

        TaskCompletionSource<object> m_taskCompletionSourceTypeless;
        Task<object> IAsyncOperation.Task // 有具体类型,用object代替的Task
        {
            get
            {
                if (m_taskCompletionSourceTypeless == null)
                {
                    m_taskCompletionSourceTypeless = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (IsDone && !CompletedEventHasListeners)
                        m_taskCompletionSourceTypeless.SetResult(Result);
                }
                return m_taskCompletionSourceTypeless.Task;
            }
        }

        /// <summary>
        /// Converts the information about the operation to a formatted string.
        /// </summary>
        /// <returns>Returns the information about the operation.</returns>
        public override string ToString()
        {
            var instId = "";
            var or = Result as Object;
            if (or != null)
                instId = "(" + or.GetInstanceID() + ")";
            return string.Format("{0}, result='{1}', status='{2}'", base.ToString(), (or + instId), m_Status);
        }

        bool m_InDeferredCallbackQueue;
        void RegisterToCompleteList(bool incrementReferenceCount = true)
        {
            // 异步操作执行完毕，不管成功还是失败，都可能进入延迟完毕的队列
            // 这个队列至少会延迟一帧
            if (IsDone && !m_InDeferredCallbackQueue) // 如果已完成 并且 不在队列中，那未完成呢？
            {
                m_InDeferredCallbackQueue = true;
                m_RM.RegisterForDeferredCallback(this, incrementReferenceCount);
            }
        }

        internal event Action<AsyncOperationHandle<TObject>> Completed
        {
            add
            {
                // 添加回调，就会尝试添加到延迟队列中
                if (this.m_CompletedActionTDelList == null)
                    this.m_CompletedActionTDelList = DelegateList<AsyncOperationHandle<TObject>>.CreateWithGlobalCache();
                this.m_CompletedActionTDelList.Add(value);
                this.RegisterToCompleteList();
            }
            remove
            {
                this.m_CompletedActionTDelList?.Remove(value);
            }
        }
        
        internal event Action<AsyncOperationHandle> CompletedTypeless
        {   // 内部使用的依然是Completed
            add
            {
                Completed += s => value(s);
            }
            remove
            {
                Completed -= s => value(s);
            }
        }

        internal event Action<AsyncOperationHandle> Destroyed
        {
            add
            {
                if (this.m_DestroyedActionDelList == null)
                    this.m_DestroyedActionDelList = DelegateList<AsyncOperationHandle>.CreateWithGlobalCache();
                this.m_DestroyedActionDelList.Add(value);
            }
            remove
            {
                this.m_DestroyedActionDelList?.Remove(value);
            }
        }
        
        Exception m_Error;
        
        /// <inheritdoc />
        internal AsyncOperationStatus Status { get { return m_Status; } }
        /// <inheritdoc />
        internal Exception OperationException
        {
            get { return m_Error; }
            private set
            {
                m_Error = value;
                if (m_Error != null && ResourceManager.ExceptionHandler != null) {
                    ResourceManager.ExceptionHandler(new AsyncOperationHandle(this), value);
                }
            }
        }
        internal bool MoveNext() { return !IsDone; }
        internal void Reset() {}
        internal object Current { get { return null; } } // should throw exception?
        internal bool IsDone { get { return Status == AsyncOperationStatus.Failed || Status == AsyncOperationStatus.Succeeded; } }
        internal float PercentComplete
        {
            get
            {
                if (m_Status == AsyncOperationStatus.None)
                {
                    try
                    {
                        return Progress;
                    }
                    catch
                    {
                        return 0.0f;
                    }
                }
                return 1.0f;
            }
        }
        
        internal AsyncOperationHandle<TObject> Handle { get { return new AsyncOperationHandle<TObject>(this); } }

        private void UpdateCallback(float unscaledDeltaTime)
        {
            IUpdateReceiver updateOp = this as IUpdateReceiver;
            updateOp.Update(unscaledDeltaTime);
        }

        /// <summary>
        /// Complete the operation and invoke events.
        /// </summary>
        /// <remarks>
        /// An operation is considered to have failed silently if success is true and if errorMsg isn't null or empty.
        /// The exception handler will be called in cases of silent failures.
        /// Any failed operations will call Release on any dependencies that succeeded.
        /// </remarks>
        /// <param name="result">The result object for the operation.</param>
        /// <param name="success">True if successful or if the operation failed silently.</param>
        /// <param name="errorMsg">The error message if the operation has failed.</param>
        public void Complete(TObject result, bool success, string errorMsg)
        {
            Complete(result, success, errorMsg, true);
        }

        /// <summary>
        /// Complete the operation and invoke events.
        /// </summary>
        /// <remarks>
        /// An operation is considered to have failed silently if success is true and if errorMsg isn't null or empty.
        /// The exception handler will be called in cases of silent failures.
        /// </remarks>
        /// <param name="result">The result object for the operation.</param>
        /// <param name="success">True if successful or if the operation failed silently.</param>
        /// <param name="errorMsg">The error message if the operation has failed.</param>
        /// <param name="releaseDependenciesOnFailure">When true, failed operations will release any dependencies that succeeded.</param>
        public void Complete(TObject result, bool success, string errorMsg, bool releaseDependenciesOnFailure)
        {
            Complete(result, success, !string.IsNullOrEmpty(errorMsg) ? new OperationException(errorMsg) : null, releaseDependenciesOnFailure);
        }

        /// <summary>
        /// Complete the operation and invoke events.
        /// </summary>
        /// <remarks>
        /// An operation is considered to have failed silently if success is true and if exception isn't null.
        /// The exception handler will be called in cases of silent failures.
        /// </remarks>
        /// <param name="result">The result object for the operation.</param>
        /// <param name="success">True if successful or if the operation failed silently.</param>
        /// <param name="exception">The exception if the operation has failed.</param>
        /// <param name="releaseDependenciesOnFailure">When true, failed operations will release any dependencies that succeeded.</param>
        /// 最终完成的回调，这个函数由 外部调用
        public void Complete(TObject result, bool success, Exception exception, bool releaseDependenciesOnFailure = true)
        {
            if (IsDone) // 已完成，则不进入，如果可以进入，可能在Update中调用的时候出现问题
                return;

            IUpdateReceiver upOp = this as IUpdateReceiver;
            if (this.m_UpdateDelList != null && upOp != null)
                this.m_UpdateDelList.Remove(m_UpdateCallback);

            Result = result;
            m_Status = success ? AsyncOperationStatus.Succeeded : AsyncOperationStatus.Failed;

            if (m_RM != null && m_RM.postProfilerEvents)
            {
                m_RM.PostDiagnosticEvent(new ResourceManager.DiagnosticEventContext(new AsyncOperationHandle(this), ResourceManager.DiagnosticEventType.AsyncOperationPercentComplete, 1));
                m_RM.PostDiagnosticEvent(new ResourceManager.DiagnosticEventContext(new AsyncOperationHandle(this), ResourceManager.DiagnosticEventType.AsyncOperationComplete));
            }

            if (m_Status == AsyncOperationStatus.Failed || exception != null)
            {
                if (exception == null || string.IsNullOrEmpty(exception.Message))
                    OperationException = new OperationException($"Unknown error in AsyncOperation : {DebugName}");
                else
                    OperationException = exception;
            }

            if (m_Status == AsyncOperationStatus.Failed)
            {
                if (releaseDependenciesOnFailure)
                    ReleaseDependencies();

                if (m_RM != null && m_RM.postProfilerEvents)
                    m_RM.PostDiagnosticEvent(new ResourceManager.DiagnosticEventContext(new AsyncOperationHandle(this), ResourceManager.DiagnosticEventType.AsyncOperationFail, 0, exception?.ToString()));

                ICachable cachedOperation = this as ICachable;
                if (cachedOperation?.Key != null)
                    m_RM?.RemoveOperationFromCache(cachedOperation.Key);

                // 加载失败，也会进入deferlist
                this.RegisterToCompleteList(false);
            }
            else
            {
                InvokeCompletionEvent();
                DecrementReferenceCount();
            }
            
            // 完成之后设置false, Start之后设置true
            IsRunning = false;
        }
        
        internal void InvokeCompletionEvent()
        {
            if (this.m_CompletedActionTDelList != null)
            {
                this.m_CompletedActionTDelList.Invoke(new AsyncOperationHandle<TObject>(this));
                this.m_CompletedActionTDelList.Clear();
            }
            
            // 处理线程result
            if (m_taskCompletionSource != null)
                m_taskCompletionSource.TrySetResult(Result);

            if (m_taskCompletionSourceTypeless != null)
                m_taskCompletionSourceTypeless.TrySetResult(Result);

            // 设置不在deferlist中
            m_InDeferredCallbackQueue = false;
        }

        // dependency是当前op的依赖handler
        internal void Start(ResourceManager rm, AsyncOperationHandle dependency, DelegateList<float> updateCallbacks)
        {
            m_RM = rm;
            // 完成之后设置false, Start之后设置true
            IsRunning = true;
            HasExecuted = false;
            if (m_RM != null && m_RM.postProfilerEvents)
            {
                m_RM.PostDiagnosticEvent(new ResourceManager.DiagnosticEventContext(new AsyncOperationHandle(this), ResourceManager.DiagnosticEventType.AsyncOperationCreate));
                m_RM.PostDiagnosticEvent(new ResourceManager.DiagnosticEventContext(new AsyncOperationHandle(this), ResourceManager.DiagnosticEventType.AsyncOperationPercentComplete, 0));
            }

            // keep a reference until the operation completes
            IncrementReferenceCount(); 
            this.m_UpdateDelList = updateCallbacks;
            
            // 如果依赖已经完成了，则InvokeExecute，否则注册到Completed，其实最终注册到了ResourceManager的DeferCallbacks队列中了
            if (dependency.IsValid() && !dependency.IsDone)
            {
                dependency.Completed += m_dependencyCompleteAction;
            }
            else // dependency是无效的依赖或者依赖已经加载完成
            {
                InvokeExecute();
            }
        }

        internal void InvokeExecute()
        {
            this.WhenDependentCompleted();
            
            HasExecuted = true;
            
            IUpdateReceiver upOp = this as IUpdateReceiver;
            if (upOp != null)
                this.m_UpdateDelList.Add(m_UpdateCallback);
        }

        event Action<AsyncOperationHandle> IAsyncOperation.CompletedTypeless
        {
            add { CompletedTypeless += value; }
            remove { CompletedTypeless -= value; }
        }

        event Action<AsyncOperationHandle> IAsyncOperation.Destroyed
        {
            add
            {
                Destroyed += value;
            }

            remove
            {
                Destroyed -= value;
            }
        }

        int IAsyncOperation.Version  => Version;

        int IAsyncOperation.ReferenceCount => ReferenceCount;

        float IAsyncOperation.PercentComplete => PercentComplete;

        AsyncOperationStatus IAsyncOperation.Status => Status;

        Exception IAsyncOperation.OperationException => OperationException;

        bool IAsyncOperation.IsDone => IsDone;

        AsyncOperationHandle IAsyncOperation.Handle => Handle;

        Action<IAsyncOperation> IAsyncOperation.OnDestroy { set { OnDestroy = value; } }

        string IAsyncOperation.DebugName => DebugName;

        /// <inheritdoc/>
        object IAsyncOperation.GetResultAsObject() => Result;

        Type IAsyncOperation.ResultType { get { return typeof(TObject); } }

        /// <inheritdoc/>
        void IAsyncOperation.GetDependencies(List<AsyncOperationHandle> deps) => GetDependencies(deps);

        /// <inheritdoc/>
        void IAsyncOperation.DecrementReferenceCount() => DecrementReferenceCount();

        /// <inheritdoc/>
        void IAsyncOperation.IncrementReferenceCount() => IncrementReferenceCount();

        /// <inheritdoc/>
        void IAsyncOperation.InvokeCompletionEvent() => InvokeCompletionEvent();

        /// <inheritdoc/>
        void IAsyncOperation.Start(ResourceManager rm, AsyncOperationHandle dependency, DelegateList<float> updateCallbacks) => Start(rm, dependency, updateCallbacks);

        internal virtual void ReleaseDependencies() {}

        /// <inheritdoc/>
        DownloadStatus IAsyncOperation.GetDownloadStatus(HashSet<object> visited) => GetDownloadStatus(visited);

        internal virtual DownloadStatus GetDownloadStatus(HashSet<object> visited)
        {
            visited.Add(this);
            return new DownloadStatus() { IsDone = IsDone };
        }
    }
}
