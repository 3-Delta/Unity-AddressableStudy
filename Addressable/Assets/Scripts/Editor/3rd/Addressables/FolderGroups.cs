using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
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

        public string FolderName {
            get { return folder.name; }
        }

        public string[] splitFilters {
            get { return searchFilters.Split('|'); }
        }

        public bool Valid {
            get { return this.folder != null && Directory.Exists(this.FullPath); }
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

    public List<FolderGroup> Groups = new List<FolderGroup>();

    public List<FolderGroup> ValidGroups {
        get {
            List<FolderGroup> ret = new List<FolderGroup>();
            foreach (var group in this.Groups) {
                if (group.Valid) {
                    ret.Add(group);
                }
            }

            return ret;
        }
    }

    public static FolderGroups Load(string assetPath) {
        return AssetDatabase.LoadAssetAtPath<FolderGroups>(assetPath);
    }
}

// 参考 AddressableAssetSettingsInspector
