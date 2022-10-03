using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PathUtils {
    public static string GetAssetPath(string fileFullPath) {
        var path = fileFullPath.Replace(Application.dataPath, "Assets");
        return path;
    }
}
