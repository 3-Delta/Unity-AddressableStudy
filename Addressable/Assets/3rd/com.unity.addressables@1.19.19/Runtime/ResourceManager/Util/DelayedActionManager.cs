using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;

namespace UnityEngine.ResourceManagement.Util
{
    // DelayedActionManager就是给Editor下同步操作提供一个delay, 模仿异步操作的延迟的
    // 通过查找引用，都是在Editor下使用的
    internal class DelayedActionManager : ComponentSingleton<DelayedActionManager>
    {
        struct DelegateInfo
        {
            static int s_Id;
            int m_Id;
            Delegate m_Delegate;
            object[] m_Target;
            public DelegateInfo(Delegate d, float invocationTime, params object[] p)
            {
                m_Delegate = d;
                m_Id = s_Id++;
                m_Target = p;
                
                // InvocationTime == Time.unscaledTime + delay
                InvocationTime = invocationTime;
            }

            public float InvocationTime { get; private set; }
            public override string ToString()
            {
                if (m_Delegate == null || m_Delegate.Method.DeclaringType == null)
                    return "Null m_delegate for " + m_Id;
                var n = m_Id + " (target=" + m_Delegate.Target + ") " + m_Delegate.Method.DeclaringType.Name + "." + m_Delegate.Method.Name + "(";
                var sep = "";
                foreach (var o in m_Target)
                {
                    n += sep + o;
                    sep = ", ";
                }
                return n + ") @" + InvocationTime;
            }

            public void Invoke()
            {
                try
                {
                    m_Delegate.DynamicInvoke(m_Target);
                }
                catch (Exception e)
                {
                    Debug.LogErrorFormat("Exception thrown in DynamicInvoke: {0} {1}", e, this);
                }
            }
        }
        
        // 非延迟action
        List<DelegateInfo>[] m_Actions = {
            new List<DelegateInfo>(), 
            new List<DelegateInfo>()
        };
        // 延迟action, delay从小到大排序
        LinkedList<DelegateInfo> m_DelayedActions = new LinkedList<DelegateInfo>();
        
        Stack<LinkedListNode<DelegateInfo>> nodePool = new Stack<LinkedListNode<DelegateInfo>>(10);
        
        // 其实可以忽略这个index,直接Update中一次性循环迭代下去。但是为了Delay模拟的更好，可以延迟2帧的情况，所以这里设计了这个index
        int m_CollectionIndex;
        bool m_DestroyOnCompletion;
        
        LinkedListNode<DelegateInfo> GetNode(ref DelegateInfo del)
        {
            if (this.nodePool.Count > 0)
            {
                var node = this.nodePool.Pop();
                node.Value = del;
                return node;
            }
            return new LinkedListNode<DelegateInfo>(del);
        }

        public static void Clear()
        {
            if (Exists)
                Instance.DestroyWhenComplete();
        }

        void DestroyWhenComplete()
        {
            m_DestroyOnCompletion = true;
        }

        public static void AddAction(Delegate action, float delay = 0, params object[] parameters)
        {
            Instance.AddActionInternal(action, delay, parameters);
        }

        void AddActionInternal(Delegate action, float delay, params object[] parameters)
        {
            var del = new DelegateInfo(action, Time.unscaledTime + delay, parameters);
            if (delay > 0)
            {
                // 延迟action
                if (m_DelayedActions.Count == 0)
                {
                    m_DelayedActions.AddFirst(GetNode(ref del));
                }
                else
                {
                    var n = m_DelayedActions.Last;
                    while (n != null && n.Value.InvocationTime > del.InvocationTime)
                        n = n.Previous;
                    if (n == null)
                        m_DelayedActions.AddFirst(GetNode(ref del));
                    else
                        m_DelayedActions.AddBefore(n, GetNode(ref del));
                }
            }
            else {
                // 非延迟action
                m_Actions[m_CollectionIndex].Add(del);
            }
        }

#if UNITY_EDITOR
        // void Awake()
        // {
        //     if (!Application.isPlaying)
        //     {
        //         // Debug.Log("DelayedActionManager called outside of play mode, registering with EditorApplication.update.");
        //         EditorApplication.update += LateUpdate;
        //     }
        // }

#endif

        public static bool IsActive
        {
            get
            {
                if (!Exists)
                    return false;
                if (Instance.m_DelayedActions.Count > 0)
                    return true;
                for (int i = 0; i < Instance.m_Actions.Length; i++)
                    if (Instance.m_Actions[i].Count > 0)
                        return true;
                return false;
            }
        }

        public static bool Wait(float timeout = 0, float timeAdvanceAmount = 0)
        {
            if (!IsActive)
                return true;

            var timer = new Stopwatch();
            timer.Start();
            var t = Time.unscaledTime;
            do
            {
                Instance.InternalLateUpdate(t);
                if (timeAdvanceAmount >= 0)
                    t += timeAdvanceAmount;
                else
                    t = Time.unscaledTime;
            }
            while (IsActive && (timeout <= 0 || timer.Elapsed.TotalSeconds < timeout));
            return !IsActive;
        }

        // 两部分功能：1 将delayaction转移到action. 2 一次执行action
        void LateUpdate() // 编辑器下EditorApplication.update驱动，runtime下正常Update驱动
        {
            InternalLateUpdate(Time.unscaledTime);
        }

        void InternalLateUpdate(float t)
        {
            int iterationCount = 0;
            while (m_DelayedActions.Count > 0 && m_DelayedActions.First.Value.InvocationTime <= t)
            {
                // 从delayaction队列转移到action队列
                m_Actions[m_CollectionIndex].Add(m_DelayedActions.First.Value);
                
                this.nodePool.Push(m_DelayedActions.First);
                m_DelayedActions.RemoveFirst();
            }

            do
            {
                int invokeIndex = m_CollectionIndex;
                m_CollectionIndex = (m_CollectionIndex + 1) % 2;
                var list = m_Actions[invokeIndex];
                if (list.Count > 0)
                {
                    for (int i = 0; i < list.Count; i++)
                        list[i].Invoke();
                    list.Clear();
                }
                iterationCount++;
                Debug.Assert(iterationCount < 100);
            }
            while (m_Actions[m_CollectionIndex].Count > 0);

            if (m_DestroyOnCompletion && !IsActive)
                Destroy(gameObject);
        }

        private void OnApplicationQuit()
        {
            if (Exists)
                Destroy(Instance.gameObject);
        }
    }
}
