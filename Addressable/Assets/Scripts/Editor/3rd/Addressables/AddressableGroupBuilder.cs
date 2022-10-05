using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UO = UnityEngine.Object;

public class AddressableGroupBuilder {
    [MenuItem("__Tools__/Addressables/ClearGroups")]
    public static AddressableAssetSettings ClearGroups() {
        AddressableAssetSettings aaSettings = AddressableAssetSettingsDefaultObject.GetSettings(false);
        if (aaSettings) {
            // 清除group
            for (int i = aaSettings.groups.Count - 1; i >= 0; --i) {
                var group = aaSettings.groups[i];
                // 避开内置的built in
                // 删除自定义的
                // 清理默认的
                if (group.ReadOnly) {
                    continue;
                }
                else if (!group.Default) {
                    aaSettings.RemoveGroup(group);
                }
                else {
                    AddressableAssetEntry[] entries = new AddressableAssetEntry[group.entries.Count];
                    group.entries.CopyTo(entries, 0);
                    for (int j = entries.Length - 1; j >= 0; --j) {
                        group.entries.Remove(entries[i]);
                    }
                }
            }

            // 清除label
            var aaLabels = aaSettings.GetLabels();
            for (int i = aaLabels.Count - 1; i >= 0; --i) {
                aaSettings.RemoveLabel(aaLabels[i]);
            }

            aaSettings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        }

        return aaSettings;
    }

    [MenuItem("__Tools__/Addressables/RebuildGroups(remote)")]
    public static void RebuildGroupsForRemote() {
        AddressableAssetSettings aaSettings = ClearGroups();
        FolderGroups fg = FolderGroups.Load("Assets/AddressableAssetsData/FolderGroups.asset");
        BuildGroups(fg, aaSettings, false);
    }

    [MenuItem("__Tools__/Addressables/RebuildGroups(local)")]
    public static void RebuildGroupsForLocal() {
        AddressableAssetSettings aaSettings = ClearGroups();
        FolderGroups fg = FolderGroups.Load("Assets/AddressableAssetsData/FolderGroups.asset");
        BuildGroups(fg, aaSettings, true);
    }

    public static void BuildGroups(FolderGroups fg, AddressableAssetSettings aaSettings, bool setAsLocalOrRemote = true) {
        var folders = fg.ValidGroups(true);
        HashSet<string> fixedAssets = new HashSet<string>();
        List<string> ignoreAssets = new List<string>();
        // assetPath:refCount
        Dictionary<string, int> dependencyDict = new Dictionary<string, int>();
        for (int i = 0, length = folders.Count; i < length; ++i) {
            var folder = folders[i];
            var files = folder.GetFiles();
            var group = CreateGroup(folder, aaSettings, setAsLocalOrRemote);

            for (int j = files.Count - 1; j >= 0; --j) {
                var fileFullPath = files[j];
                CreateEntries(group, aaSettings, fileFullPath, folder);

                if (fg.includeDependency) {
                    var assetPath = PathUtils.GetAssetPath(fileFullPath);
                    if (!fixedAssets.Contains(assetPath)) {
                        fixedAssets.Add(assetPath);
                    }
                }
            }
        }

        if (fg.includeDependency) {
            CollectDependencies(in fixedAssets, dependencyDict, ignoreAssets);

            // 打印过滤文件
            foreach (var asset in ignoreAssets) {
                Debug.Log("过滤了文件/文件夹: " + asset);
            }

            foreach (var kvp in dependencyDict) {
                // refCount >= 2
                if (kvp.Value >= 2) { }
            }
        }

        AddressableAssetGroup CreateGroup(FolderGroups.FolderGroup folder, AddressableAssetSettings aaSettings, bool setAsLocalOrRemote) {
            string groupName = folder.FolderName;
            var group = aaSettings.FindGroup(groupName);
            if (group == null) {
                // https://docs.unity3d.com/Packages/com.unity.addressables@1.19/manual/GroupSettings.html
                // When you create a group with the Packed Assets template, the Content Packing & Loading and Content Update Restriction schemas define the settings for the group.
                group = aaSettings.CreateGroup(groupName, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                var schemaBundle = group.GetSchema<BundledAssetGroupSchema>();

                // 获取当前的profile
                // aaSettings.profileSettings.GetProfile(aaSettings.activeProfileId);

                if (setAsLocalOrRemote) {
                    schemaBundle.BuildPath.SetVariableByName(aaSettings, AddressableAssetSettings.kLocalBuildPath);
                    schemaBundle.LoadPath.SetVariableByName(aaSettings, AddressableAssetSettings.kLocalLoadPath);
                }
                else {
                    schemaBundle.BuildPath.SetVariableByName(aaSettings, AddressableAssetSettings.kRemoteBuildPath);
                    schemaBundle.LoadPath.SetVariableByName(aaSettings, AddressableAssetSettings.kRemoteLoadPath);
                }

                // aa构建时包含此group
                schemaBundle.IncludeInBuild = true;
                // 使用group.guid 和 Application.cloudProjectId同时控制
                schemaBundle.InternalBundleIdMode = BundledAssetGroupSchema.BundleInternalIdMode.GroupGuidProjectIdHash;
                
                schemaBundle.UseAssetBundleCache = true;
                // catalog中记录资源的address，方面后面运行时可以根据address进行加载
                // https://docs.unity3d.com/Packages/com.unity.addressables@1.19/manual/GetRuntimeAddress.html
                schemaBundle.IncludeAddressInCatalog = true; 
                schemaBundle.IncludeGUIDInCatalog = false;
                // 不记录label, 应该会导致后面根据label加载资源的时候失败
                schemaBundle.IncludeLabelsInCatalog = false;
                
                schemaBundle.UseAssetBundleCrc = false; // 关闭crc
                schemaBundle.UseAssetBundleCrcForCachedBundles = true;

                schemaBundle.RetryCount = 3;
                schemaBundle.Timeout = 10;
                schemaBundle.AssetBundledCacheClearBehavior = BundledAssetGroupSchema.CacheClearBehavior.ClearWhenSpaceIsNeededInCache;
                // https://docs.unity3d.com/Packages/com.unity.addressables@1.19/manual/LoadingAssetBundles.html
                // 默认情况下本地资源用AssetBundle.LoadFromFileAsync,远程资源用UnityWebRequest
                schemaBundle.UseUnityWebRequestForLocalBundles = false;
                schemaBundle.InternalIdNamingMode = BundledAssetGroupSchema.AssetNamingMode.FullPath; // bundle内部资源的命名模式
                // AppendHash会有文件夹的层次结构
                schemaBundle.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.AppendHash; // hash作为bundle name
                // FileNameHash 平铺，同时bundle的命名只有hash
                schemaBundle.BundleMode = folder.allInOne ? BundledAssetGroupSchema.BundlePackingMode.PackTogether : BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
                
                var schemaContent = group.GetSchema<ContentUpdateGroupSchema>();
                // StaticContent = newType == ContentType.CannotChangePostRelease;
                schemaContent.StaticContent = true; // 静态资源
            }

            return group;
        }

        void CreateEntries(AddressableAssetGroup group, AddressableAssetSettings aaSettings, string fileFullPath, FolderGroups.FolderGroup folder) {
            var assetPath = PathUtils.GetAssetPath(fileFullPath);
            var assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            var entry = aaSettings.CreateOrMoveEntry(assetGuid, group);
            var prefix = aaSettings.profileSettings.GetValueByKeyName("AddressableResourcesAssetPath", "Default");
            string path = assetPath.Replace($"{prefix}/{folder.FolderName}/", "");
            entry.address = path;
        }

        void CollectDependencies(in HashSet<string> fixedAssets, Dictionary<string, int> dependencyDict, List<string> ignoreAssets) {
            foreach (var assetPath in fixedAssets) {
                Collect(assetPath, in fixedAssets, dependencyDict, ignoreAssets);
            }
        }

        void Collect(string assetPath, in HashSet<string> fixedAssets, Dictionary<string, int> dependencyDict, List<string> ignoreAssets) {
            // EditorUtility.CollectDependencies
            string[] deps = AssetDatabase.GetDependencies(assetPath, false);
            for (int i = 0, length = deps.Length; i < length; ++i) {
                var depAssetPath = deps[i];
                if (assetPath.Equals(depAssetPath, StringComparison.Ordinal)) {
                    continue;
                }

                // 已经存在
                if (fixedAssets.Contains(depAssetPath)) {
                    continue;
                }

                // 文件夾过滤
                if (depAssetPath.Contains("Gizmos", StringComparison.Ordinal) || depAssetPath.Contains("Editor", StringComparison.Ordinal) || depAssetPath.Contains("Plugins", StringComparison.Ordinal) || depAssetPath.Contains("Presets", StringComparison.Ordinal) || depAssetPath.Contains("BuildReports", StringComparison.Ordinal) ||
                    // https://docs.unity3d.com/Packages/com.unity.addressables@1.19/manual/ManagingAssets.html
                    // address不能使用resources下面的文件
                    depAssetPath.Contains("Resources", StringComparison.Ordinal)) {
                    ignoreAssets.Add(depAssetPath);
                    continue;
                }

                // 文件过滤
                if (depAssetPath.EndsWith(".cs", StringComparison.Ordinal) || depAssetPath.EndsWith(".dll", StringComparison.Ordinal) || depAssetPath.EndsWith(".so", StringComparison.Ordinal) || depAssetPath.EndsWith(".pdb", StringComparison.Ordinal)) {
                    ignoreAssets.Add(depAssetPath);
                    continue;
                }

                dependencyDict.TryGetValue(depAssetPath, out int refCount);
                dependencyDict[depAssetPath] = refCount + 1;
            }
        }
    }

    [MenuItem("__Tools__/Addressables/Build AA(default)")]
    public static void DefauldBuild() {
        var builders = AddressableAssetSettingsDefaultObject.Settings.DataBuilders;
        int index = builders.IndexOf(builders.Find(s => s.GetType() == typeof(BuildScriptPackedMode)));

        AddressableAssetSettingsDefaultObject.Settings.ActivePlayerDataBuilderIndex = index;
        AddressableAssetSettings.BuildPlayerContent();
    }
}

public static class AddressableAssetProfileSettingsExt {
    public static string GetValueByKeyName(this AddressableAssetProfileSettings settings, string keyName, string prifileName = "Default") {
        var profileId = settings.GetProfileId(prifileName);
        if (profileId != null) {
            return settings.GetValueByName(profileId, keyName);
        }

        return null;
    }
}
