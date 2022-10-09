using System;
using System.Collections.Generic;
using UnityEngine.ResourceManagement.Util;

internal class DelegateList<T>
{
    Func<Action<T>, LinkedListNode<Action<T>>> m_acquireFunc;
    Action<LinkedListNode<Action<T>>> m_releaseFunc;
    
    LinkedList<Action<T>> m_callbackList;
    bool m_invoking = false;
    public DelegateList(Func<Action<T>, LinkedListNode<Action<T>>> acquireFunc, Action<LinkedListNode<Action<T>>> releaseFunc)
    {
        if (acquireFunc == null)
            throw new ArgumentNullException("acquireFunc");
        if (releaseFunc == null)
            throw new ArgumentNullException("releaseFunc");
        m_acquireFunc = acquireFunc;
        m_releaseFunc = releaseFunc;
    }

    public int Count { get { return this.m_callbackList == null ? 0 : this.m_callbackList.Count; } }

    public void Add(Action<T> action)
    {
        var node = m_acquireFunc(action);
        if (this.m_callbackList == null)
            this.m_callbackList = new LinkedList<Action<T>>();
        this.m_callbackList.AddLast(node);
    }

    public void Remove(Action<T> action)
    {
        if (this.m_callbackList == null)
            return;

        var node = this.m_callbackList.First;
        while (node != null)
        {
            if (node.Value == action)
            {
                if (m_invoking)
                {
                    node.Value = null;
                }
                else
                {
                    this.m_callbackList.Remove(node);
                    m_releaseFunc(node);
                }
                return;
            }
            node = node.Next;
        }
    }

    public void Invoke(T res)
    {
        if (this.m_callbackList == null)
            return;

        m_invoking = true;
        var node = this.m_callbackList.First;
        while (node != null)
        {
            if (node.Value != null)
            {
                try
                {
                    node.Value?.Invoke(res);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                }
            }
            node = node.Next;
        }
        m_invoking = false;
        
        // 清除list中value为null的项
        node = this.m_callbackList.First;
        while (node != null)
        {
            var next = node.Next;
            if (node.Value == null)
            {
                this.m_callbackList.Remove(node);
                m_releaseFunc(node);
            }
            node = next;
        }
    }

    public void Clear()
    {
        if (this.m_callbackList == null)
            return;
        var node = this.m_callbackList.First;
        while (node != null)
        {
            var next = node.Next;
            this.m_callbackList.Remove(node);
            m_releaseFunc(node);
            node = next;
        }
    }

    public static DelegateList<T> CreateWithGlobalCache()
    {
        return new DelegateList<T>(GlobalLinkedListNodeCache<Action<T>>.Acquire, GlobalLinkedListNodeCache<Action<T>>.Release);
    }
}
