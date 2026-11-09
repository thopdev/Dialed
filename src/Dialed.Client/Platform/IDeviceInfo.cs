namespace Dialed.Client.Platform;

/// <summary>Describes the device and how the app runs on it (for feedback messages), e.g. "iPhone, iOS 18.1, installed app". No identifiers.</summary>
public interface IDeviceInfo
{
    Task<string> DescribeAsync();
}
