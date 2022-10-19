using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;
using UO = UnityEngine.Object;

// 继承自ScriptableObject类，必须和monobehaviour一样，一个文件一个类，类名和文件名一样
[Serializable]
[CreateAssetMenu(menuName = "Addressables/FolderGroups", fileName = nameof(FolderGroups) + ".asset")]
public class FolderGroups : ScriptableObject {
    [Serializable]
    public class FolderGroup {
        public DefaultAsset folder;
        public string searchFilters;
        public SearchOption searchOption = SearchOption.AllDirectories;
        public bool allInOne = false;
        public bool includeInBuild = true;

        public string FolderName {
            get {
                if (folder) {
                    return folder.name;
                }

                return null;
            }
        }

        public string[] splitFilters {
            get { return searchFilters.Split('|'); }
        }

        public bool Valid {
            get { return Directory.Exists(this.FullPath); }
        }

        public string AssetPath {
            get {
                if (this.folder != null) {
                    return AssetDatabase.GetAssetOrScenePath(this.folder);
                }

                return null;
            }
        }

        public string FullPath {
            get {
                var assetPath = this.AssetPath;
                if (assetPath != null) {
                    return $"{Application.dataPath.Replace("Assets", "")}{assetPath}";
                }

                return null;
            }
        }

        public FolderGroup(DefaultAsset folder, string searchFilters, SearchOption searchOption = SearchOption.AllDirectories, bool allInOne = false) {
            this.folder = folder;
            this.searchFilters = searchFilters;
            this.searchOption = searchOption;
            this.allInOne = allInOne;
        }

        // public bool includeDependencies = false;
        public List<string> GetFiles(bool filterMeta = true) {
            List<string> fis = new List<string>();
            var filters = splitFilters;
            for (int i = 0, length = filters.Length; i < length; ++i) {
                var f = filters[i];
                var range = Directory.GetFiles(this.FullPath, "*." + f, this.searchOption);
                fis.AddRange(range);
            }

            for (int i = fis.Count - 1; i >= 0; --i) {
                fis[i] = fis[i].Replace("\\", "/");
                if (filterMeta) {
                    var fi = new FileInfo(fis[i]);
                    if (fi.Extension.Equals(".meta", StringComparison.Ordinal)) {
                        // 过滤meta
                        fis.RemoveAt(i);
                    }
                }
            }

            return fis;
        }
    }

    // refCount > 1的整理
    public bool includeDependency = false;
    // buildplayer的时候全部认为是local，转移到StreamingAsset下
    // 如果后续有更新需求，则设置为remote
    // remote资源，aa是用Unity的cache管理的，而我们需要将其存储在persistent下。
    // public bool setAsLocalOrRemote = true;
    public List<FolderGroup> DedenpendentGroups = new List<FolderGroup>();
    // 没有依赖的资源配置，比如音效, 将来如果增量热更的话，可以直接只build这个group, 不需要重新构建所有的group,减少构建时间
    public List<FolderGroup> IndenpendentGroups = new List<FolderGroup>();

    public List<FolderGroup> ValidGroups(in List<FolderGroup> target, bool onlyIncludeInBuild = true) {
        List<FolderGroup> ret = new List<FolderGroup>();
        foreach (var group in target) {
            if (group.Valid) {
                if (!onlyIncludeInBuild || group.includeInBuild) {
                    ret.Add(group);
                }
            }
        }

        return ret;
    }

    public bool Get(string folderName, out FolderGroup group) {
        group = default;
        var ret = this.DedenpendentGroups.Find(g => g.FolderName.Equals(folderName, StringComparison.Ordinal));
        if (ret == null) {
            ret = this.IndenpendentGroups.Find(g => g.FolderName.Equals(folderName, StringComparison.Ordinal));
        }

        return ret != null;
    }

    public static FolderGroups Load(string assetPath) {
        return AssetDatabase.LoadAssetAtPath<FolderGroups>(assetPath);
    }
}

// 参考 AddressableAssetSettingsInspector
