using System;
using UnityEngine;
using UnityEngine.ResourceManagement.Util;

internal class MonoBehaviourCallbackHooks : ComponentSingleton<MonoBehaviourCallbackHooks>
{
    internal Action<float> m_OnUpdateDelegate;
    public event Action<float> OnUpdateDelegate // 其实执行的是ResourceManager的Update
    {
        add
        {
            m_OnUpdateDelegate += value;
        }

        remove
        {
            m_OnUpdateDelegate -= value;
        }
    }

    protected override string GetGameObjectName() => "ResourceManagerCallbacks";

    // Update is called once per frame
    internal void Update()
    {
        m_OnUpdateDelegate?.Invoke(Time.unscaledDeltaTime);
    }
}
