using System;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.Util;

[DisallowMultipleComponent]
public class App : ComponentSingleton<App> {
    // 资源路径是group下资源名字
    public string path = "UI/UILogin.prefab";
    private AsyncOperationHandle<GameObject> handler;

    private void Update() {
        if (Input.GetKeyDown(KeyCode.A)) {
            AddressableService.LoadAssetAsync(ref handler, this.path, OnLoaded);
        }
    }

    private void OnLoaded(AsyncOperationHandle<GameObject> obj) {
        Debug.LogError("OnLoaded");
    }

    private void OnDestroy() {
        AddressableService.ReleaseInstance(ref this.handler, this.OnLoaded);
    }
}
