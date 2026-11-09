namespace Dialed.Client.Platform;

/// <summary>Hands a file to the person: the share sheet where it can carry files, otherwise a download. Web: Web Share API or a blob download.</summary>
public interface IFileExport
{
    /// <summary>Returns <c>false</c> when the person dismissed the share sheet.</summary>
    Task<bool> SaveAsync(string fileName, string contentType, byte[] content);
}
