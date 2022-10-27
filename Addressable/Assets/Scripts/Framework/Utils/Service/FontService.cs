using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UO = UnityEngine.Object;

public class SingleAssetService<T> where T :  UO {
    public static Dictionary<string, AsyncOperationHandle<T>> store = new Dictionary<string, AsyncOperationHandle<T>>();

    // 后面加载的时候应该要考虑到语言和地区，比如繁体语言，香港地区，粤语配音的label
    public static T Get(string key) {
        if (string.IsNullOrWhiteSpace(key)) {
            return default(T);
        }

        if (!store.TryGetValue(key, out var handler)) {
            handler = Addressables.LoadAssetAsync<T>(key);

            if (!handler.IsDone) {
                Debug.LogErrorFormat("阻塞加载： {0}", key);
                handler.WaitForCompletion();
            }

            store[key] = handler;
        }

        return handler.Result;
    }

    public static void Release(string key) {
        if (string.IsNullOrWhiteSpace(key)) {
            return;
        }

        if (store.TryGetValue(key, out var handler)) {
            Addressables.Release<T>(handler);
            store.Remove(key);
        }
    }

    public static void Clear() {
        foreach (var kvp in store) {
            var handler = kvp.Value;
            Addressables.Release<T>(kvp.Value);
        }

        store.Clear();
    }
}

public class FontService : SingleAssetService<Font> { }
