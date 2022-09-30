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

    [MenuItem("__Tools__/Addressables/RebuildGroups")]
    public static void RebuildGroups() {
        AddressableAssetSettings aaSettings = ClearGroups();
        FolderGroups fg = FolderGroups.Load("Assets/AddressableAssetsData/FolderGroups.asset");
        BuildGroups(fg, aaSettings);
    }

    public static void BuildGroups(FolderGroups fg, AddressableAssetSettings aaSettings) {
        var folders = fg.ValidGroups;
        for (int i = 0, length = folders.Count; i < length; ++i) {
            var folder = folders[i];
            var files = folder.GetFiles();
            var group = CreateGroup(folder, aaSettings);
            for (int j = files.Count - 1; j >= 0; --j) {
                CreateEntries(group, aaSettings, files[j], folder);
            }
        }

        AddressableAssetGroup CreateGroup(FolderGroups.FolderGroup folder, AddressableAssetSettings aaSettings) {
            string groupName = folder.FolderName;
            var group = aaSettings.FindGroup(groupName);
            if (group == null) {
                group = aaSettings.CreateGroup(groupName, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                var schemaBundle = group.GetSchema<BundledAssetGroupSchema>();
                schemaBundle.UseAssetBundleCrc = false; // 关闭crc
                schemaBundle.UseAssetBundleCache = true;
                schemaBundle.UseAssetBundleCrcForCachedBundles = true;
                schemaBundle.IncludeGUIDInCatalog = false;
                schemaBundle.RetryCount = 3;
                schemaBundle.Timeout = 10;
                schemaBundle.AssetBundledCacheClearBehavior = BundledAssetGroupSchema.CacheClearBehavior.ClearWhenSpaceIsNeededInCache;
                schemaBundle.InternalIdNamingMode = BundledAssetGroupSchema.AssetNamingMode.FullPath;

                var schemaContent = group.GetSchema<ContentUpdateGroupSchema>();
                schemaContent.StaticContent = false; // 重定向StreamAssets

                schemaBundle.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.FileNameHash; // hash作为bundle name
                schemaBundle.BuildPath.SetVariableByName(aaSettings, AddressableAssetSettings.kLocalBuildPath);
                schemaBundle.LoadPath.SetVariableByName(aaSettings, AddressableAssetSettings.kLocalLoadPath);
                schemaBundle.BundleMode = folder.allInOne ? BundledAssetGroupSchema.BundlePackingMode.PackTogether : BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
            }

            return group;
        }

        void CreateEntries(AddressableAssetGroup group, AddressableAssetSettings aaSettings, string fileFullPath, FolderGroups.FolderGroup folder) {
            var prefix = aaSettings.profileSettings.GetValueByKeyName("AddressableResourcesAssetPath", "Default");
            var assetPath = fileFullPath.Replace(Application.dataPath, "Assets");
            var assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            var entry = aaSettings.CreateOrMoveEntry(assetGuid, group);
            if (entry != null) {
                string path = assetPath.Replace($"{prefix}/{folder.FolderName}/", "");
                entry.address = path;
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
