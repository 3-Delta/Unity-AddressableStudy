using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public class ShaderService {
    // shader因为是多合一的，所以项目用label的方式加载
    private static AsyncOperationHandle<IList<Shader>> shaderHandler;
    private static Dictionary<string, Shader> shaders = new Dictionary<string, Shader>();

    private static AsyncOperationHandle<ShaderVariantCollection> collectionHandler;
    private static ShaderVariantCollection collection;

    public static void Load() {
        string label = "shader";

        collectionHandler = Addressables.LoadAssetAsync<ShaderVariantCollection>(label);
        collectionHandler.Completed += OnCollectionLoaded;

        AssetLabelReference assetLabel = new AssetLabelReference();
        assetLabel.labelString = label;
        shaderHandler = Addressables.LoadAssetAsync<IList<Shader>>(assetLabel);
        shaderHandler.Completed += OnShadersLoaded;
    }

    private static void OnShadersLoaded(AsyncOperationHandle<IList<Shader>> obj) {
        IList<Shader> s = obj.Result;
        shaders.Clear();

        for (int i = 0, length = s.Count; i < length; ++i) {
            shaders.Add(s[i].name, s[i]);
        }
    }

    private static void OnCollectionLoaded(AsyncOperationHandle<ShaderVariantCollection> obj) {
        collection = obj.Result;
    }

    public static bool Get(string key, out Shader ret) {
        if (!shaders.TryGetValue(key, out ret)) {
            // 热更中找不到，则包体中查找
            ret = Shader.Find(key);
        }

        return ret != null;
    }

    public static void Clear() {
        Addressables.Release(shaderHandler);
        Addressables.Release(collectionHandler);
    }

    public static void Warmup() {
        if (collection == null || collection.isWarmedUp) {
            return;
        }
        
        collection.WarmUp();
    }
}
