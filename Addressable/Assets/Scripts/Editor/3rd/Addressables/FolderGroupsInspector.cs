using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UO = UnityEngine.Object;

// [CustomEditor(typeof(FolderGroups))]
// public class FolderGroupsInspector : Editor {
//     public ReorderableList list;
//
//     private FolderGroups fgs;
//
//     private void OnEnable() {
//         var fgs = target as FolderGroups;
//         if (fgs == null) {
//             return;
//         }
//         
//         list = new ReorderableList(fgs.DedenpendentGroups, typeof(ScriptableObject), true, true, true, true);
//         list.drawElementCallback = this.DrawCallback;
//         list.headerHeight = 0;
//         // list.onAddDropdownCallback = this.OnAdd;
//         // list.onRemoveCallback = this.OnRemove;
//     }
//     
//     private void DrawCallback(Rect rect, int index, bool isActive, bool isFocused)
//     {
//         // var folder = fgs.DedenpendentGroups[index];
//         // var label = folder == null ? "" : folder.FolderName;
//         // EditorGUI.ObjectField(rect, label, folder, typeof(FolderGroups.FolderGroup), false);
//     }
//
//     private void OnRemove(ReorderableList list)
//     {
//         fgs.DedenpendentGroups.RemoveAt(list.index);
//     }
//
//     private void OnAdd(Rect buttonRect, ReorderableList list)
//     {
//     }
// }

// 参考 AddressableAssetSettingsInspector
// BundleNamingStylePropertyDrawer : PropertyDrawer
