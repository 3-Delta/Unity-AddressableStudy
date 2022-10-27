using System;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Text))]
[DisallowMultipleComponent]
public class TextRebinder : MonoBehaviour {
    public const string PREFIX = "Assets/AddressableResources/Font";

    public string fontKey;

    public Text text;

#if UNITY_EDITOR // 打包ab的时候，editor下剔除prefab的font引用，记录font的信息
    [ContextMenu(nameof(Restore))]
    public void Restore() {
        if (!this.text) {
            this.text = this.GetComponent<Text>();
        }

        if (this.text.font) {
            this.fontKey = AssetDatabase.GetAssetPath(this.text.font);
            this.fontKey = this.fontKey.Substring(PREFIX.Length);
        }
    }
#endif

    public void Start() {
        if (!this.text) {
            this.text = this.GetComponent<Text>();
        }

        if (this.text.font == null && this.fontKey != null) {
            this.text.font = FontService.Get(this.fontKey);
        }
    }
}
