namespace Lokad.Bash;

/// <summary>Controls file-version capture independently of directory metadata read-ahead.</summary>
public enum DirectorySnapshotRequest
{
    /// <summary>Return entries without capturing file versions.</summary>
    None,

    /// <summary>Include available regular-file snapshots without loading children or retrieving file bytes.</summary>
    CaptureAvailable
}
