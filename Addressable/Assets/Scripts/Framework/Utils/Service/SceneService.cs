using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UScene = UnityEngine.SceneManagement;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

public enum ESceneLoadStatus {
    Nil,
    Loading,
    Releasing,
    Success,
    Fail,
}

public class SceneEntry {
    public string name;

    public UScene.Scene scene { get; private set; }
    private ESceneLoadStatus sceneLoadStatus = ESceneLoadStatus.Nil;

    private bool active = false;
    private bool needAlive = false;

    private AsyncOperationHandle<SceneInstance> handler;

    public string Path {
        // 其实就是AddressableGroup的address
        get { return $"Scene/{this.name}.unity"; }
    }

    public bool IsDone {
        // 或者用handler代替 handler.IsDone
        get { return this.sceneLoadStatus == ESceneLoadStatus.Fail || this.sceneLoadStatus == ESceneLoadStatus.Success; }
    }

    public GameObject Root { get; private set; }

    public SceneEntry(string name) {
        this.name = name;
    }

    public bool Contains(ESceneLoadStatus loadStatus) {
        return (this.sceneLoadStatus & loadStatus) != 0;
    }

    public void SetActive(bool toActive) {
        this.active = toActive;
        this.needAlive |= this.active;

        if (this.sceneLoadStatus == ESceneLoadStatus.Success) {
            this.ShowScene();
        }
    }

    public void Load() {
        this.needAlive = true;
        _DoLoad();
    }

    public void Release() {
        this.active = false;
        this.needAlive = false;

        _DoRelease();
    }

    private void _DoLoad() {
        if (this.sceneLoadStatus != ESceneLoadStatus.Nil) {
            return;
        }

        this.sceneLoadStatus = ESceneLoadStatus.Loading;
        AddressableUtils.LoadSceneAsync(ref this.handler, Path, UScene.LoadSceneMode.Additive, true, 100, OnSceneLoaded);
    }

    private void OnSceneLoaded(AsyncOperationHandle<SceneInstance> sceneHandler) {
        // addressable内部应该处理了在回调之前-=回调的操作
        // sceneHandler.Completed -= this.OnSceneLoaded;

        this.sceneLoadStatus = sceneHandler.Status == AsyncOperationStatus.Succeeded ? ESceneLoadStatus.Success : ESceneLoadStatus.Fail;
        if (this.needAlive) {
            this.scene = sceneHandler.Result.Scene;

            this.ParseScene();
            this.ShowScene();
        }
        else {
            this._DoRelease();
        }
    }

    private void _DoRelease() {
        if (IsDone) {
            if (this.handler.IsValid()) {
                this.sceneLoadStatus = ESceneLoadStatus.Releasing;
                AddressableUtils.ReleaseScene(ref this.handler, OnSceneReleased);
            }
            else {
                this.sceneLoadStatus = ESceneLoadStatus.Nil;
            }
        }
    }

    private void OnSceneReleased(AsyncOperationHandle<SceneInstance> sceneHandler) {
        if (this.sceneLoadStatus == ESceneLoadStatus.Releasing) {
            this.sceneLoadStatus = ESceneLoadStatus.Nil;
        }

        if (this.needAlive) {
            this._DoLoad();
        }
    }

    private void ShowScene() {
        if (Root != null) {
            Root.SetActive(this.active);
        }
    }

    private void ParseScene() {
        // 处理场景的资源分析
        var gos = this.scene.GetRootGameObjects();
        for (int i = 0, length = gos.Length; i < length; ++i) {
            if (gos[i].name.Equals("SceneRoot", StringComparison.Ordinal)) {
                this.Root = gos[i];
                break;
            }
        }
    }
}

public class SceneService {
    public readonly static Dictionary<string, SceneEntry> scenes = new Dictionary<string, SceneEntry>();

    public static UScene.Scene DefaultScene { get; private set; }
    public static SceneEntry MainScene { get; private set; }

    public static void Init(UScene.Scene defaultScene) {
        // 包体中的第一个场景，一般就是非热更场景，也就是EditorSceneBuildList中的第一个
        DefaultScene = defaultScene;
    }

    public static void EventRegister(bool toRegister) {
        if (toRegister) {
            USceneManager.sceneLoaded += OnSceneLoaded;
        }
        else {
            USceneManager.sceneLoaded -= OnSceneLoaded;
        }
    }

    private static void OnSceneLoaded(UScene.Scene scene, UScene.LoadSceneMode mode) {
        // 检测相机组件，等一些关于场景的组件的设置
    }

    public static void LoadSceneAsync(string sceneName, UScene.LoadSceneMode loadMode = UScene.LoadSceneMode.Single, bool toActive = true) {
        if (string.IsNullOrWhiteSpace(sceneName)) {
            return;
        }

        if (!scenes.TryGetValue(sceneName, out SceneEntry entry)) {
            entry = new SceneEntry(sceneName);
            scenes.Add(sceneName, entry);
        }

        entry.Load();
        entry.SetActive(toActive);
        if (loadMode == UScene.LoadSceneMode.Single) {
            MainScene = entry;
            if (entry.scene.IsValid() && entry.scene.isLoaded) {
                USceneManager.SetActiveScene(entry.scene);
            }
        }
    }

    public static void ReleaseScene(string sceneName) {
        if (scenes.TryGetValue(sceneName, out SceneEntry entry)) {
            if (USceneManager.GetActiveScene() == entry.scene) {
                USceneManager.SetActiveScene(DefaultScene);
            }

            entry.Release();
        }
    }

    public static void SetMainScene(string sceneName) {
        if (sceneName.Equals(MainScene.name, StringComparison.Ordinal)) {
            return;
        }

        if (scenes.TryGetValue(sceneName, out SceneEntry entry)) {
            MainScene = entry;
            if (entry != null && entry.scene.IsValid()) {
                USceneManager.SetActiveScene(entry.scene);
            }
        }
    }

    public static void SetSceneActive(string sceneName, bool toActive) {
        if (scenes.TryGetValue(sceneName, out SceneEntry entry)) {
            entry.SetActive(toActive);
        }
    }
}
