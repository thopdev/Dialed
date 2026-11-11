using Dialed.Images;

namespace Dialed.Api.Photos;

public static class PhotoProcessingExtensions
{
    /// <summary>
    /// Photo storage and processing (#69): an in-memory blob store whose writes under <c>photos/</c> run <see cref="PhotoProcessingHandler"/>
    /// in the background. Whatever stores uploaded photos (the upload queue, #68) writes to <see cref="IPhotoBlobStore"/> and gets
    /// stripping and thumbnails for free. In Azure, Blob Storage and the Function replace the store and the trigger.
    /// </summary>
    public static IServiceCollection AddDialedPhotoProcessing(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryPhotoBlobStore>();
        services.AddSingleton<IPhotoBlobStore>(sp => sp.GetRequiredService<InMemoryPhotoBlobStore>());
        services.AddSingleton<IPhotoImageProcessor, SkiaPhotoImageProcessor>();
        services.AddSingleton<IPhotoRecords, SyncStorePhotoRecords>();
        services.AddScoped<PhotoProcessingHandler>();
        services.AddSingleton<InMemoryPhotoTrigger>();
        services.AddHostedService(sp => sp.GetRequiredService<InMemoryPhotoTrigger>());
        return services;
    }
}
