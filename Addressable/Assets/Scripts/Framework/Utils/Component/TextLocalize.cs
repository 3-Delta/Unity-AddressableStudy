using System;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Text))]
[DisallowMultipleComponent]
public class TextLocalize : MonoBehaviour {
    public uint languageId;

#if UNITY_EDITOR
    // editor下有时候需要快速预览各种语言的表现
    public SystemLanguage language = SystemLanguage.ChineseSimplified;
#endif
    
    // 在任意TextLocalize使用之前，先赋值初始化
    public static Func<uint, int, string> onGet;
    // ilruntime的魔力项目下，是使用框架层调用热更层接口的静态接口去处理的，但是其实回调的方式更加方便

    private void Start() {
        this.OnGet();
    }

#if UNITY_EDITOR
    // 如果有可能，通过在Unity的inspector下，填写excel表格内容
    public static Action<uint, int, Text> onSet;

    [ContextMenu(nameof(OnSet))]
    private void OnSet() {
        if (this.languageId != 0) {
            var text = this.GetComponent<Text>();
            onSet?.Invoke(this.languageId, (int)this.language, text);
        }
    }

    // 预览某个节点下所有的Text的内容
    public static void PreviewAll(Transform node) {
        var array = node.gameObject.GetComponentsInChildren<TextLocalize>();
        for (int i = 0, length = array.Length; i < length; ++i) {
            array[i].OnGet();
        }
    }

    [ContextMenu(nameof(OnGet))]
#endif
    public void OnGet() {
        var text = this.GetComponent<Text>();
        if (this.languageId != 0) {
#if UNITY_EDITOR
            var content = onGet?.Invoke(this.languageId, (int)this.language);
#else
            var content = onGet?.Invoke(this.languageId, (int)Application.systemLanguage);
#endif
            text.text = content;
        }
        else {
            text.text = "";
        }
    }
}
