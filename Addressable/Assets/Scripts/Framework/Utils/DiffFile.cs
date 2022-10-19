using System;
using System.Collections.Generic;

// 每次增量更新ab的时候，总是在client比对从而得到最终需要下载哪些文件，也就是哪些文件需要删除，哪些文件需要新增，哪些文件被修改了
// 可以将这部分比对预处理，在上传给webserver的时候，放置一个difffile，存储（哪些文件需要删除，哪些文件需要新增，哪些文件被修改了）
// 如果client本地的ab文件被删除或者修改了，那么也需要重新下载，也就是将client一侧的diffFile和remote的diffFile进行merge
// 另外配合addressable可以load不止一个content，这样子就可以分为加载一个base,加载一个incrment.

[Serializable]
public class FileDesc {
    public string path;
    public ulong size;
    
    public string hash;
}

[Serializable]
public class DiffFile {
    public enum EStatus {
        Added,
        Removed,
        Modified,
    }

    // added和modified的总大小
    public ulong totalChangedSize = 0;
    
    public List<FileDesc> added = new List<FileDesc>();
    public List<FileDesc> removed = new List<FileDesc>();
    // 内容变化，文件时间变化不处理
    public List<FileDesc> modified = new List<FileDesc>();

    public void Clear(EStatus status) {
        if (status == EStatus.Added) {
            added.Clear();
        }
        else if (status == EStatus.Removed) {
            removed.Clear();
        }
        else if (status == EStatus.Modified) {
            modified.Clear();
        }
    }

    // 根据两次文件列表，比对出差异化列表
    public static DiffFile Diff(List<FileDesc> left, List<FileDesc> right) {
        DiffFile outFile = new DiffFile();
        Dictionary<string, FileDesc> hash = new Dictionary<string, FileDesc>();
        return outFile;
    }

    public void Merge(in DiffFile from) {
        foreach (var file in from.added) {
            this.added.Add(file);
        }
        foreach (var file in from.removed) {
            this.removed.Add(file);
        }
        foreach (var file in from.modified) {
            this.modified.Add(file);
        }

        this.totalChangedSize += from.totalChangedSize;
    }
    
    public static DiffFile Merge(in DiffFile from, in DiffFile right) {
        DiffFile mergeFile = new DiffFile();
        mergeFile.Merge(from);
        mergeFile.Merge(right);
        return mergeFile;
    }
}
