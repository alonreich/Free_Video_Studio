using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Infrastructure;

public sealed class UserFacingIOException : IOException
{
    public UserFacingIOException(string userMessage) : base(userMessage) { }
    public UserFacingIOException(string userMessage, Exception inner) : base(userMessage, inner) { }
}

internal static class UserFacingError
{
    internal const string PassThroughTag = "ERRPLAIN_02";

    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int HandleDiskFullHResult = unchecked((int)0x80070027);
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    public static string Describe(Exception ex)
    {
        Exception root = Unwrap(ex);
        return root switch
        {
            FreeVideoStudio.App.Services.AiProviderException or FreeVideoStudio.App.Services.AiMusicAutoFitException or UserFacingIOException
                when !string.IsNullOrWhiteSpace(root.Message) => root.Message,
            HttpRequestException { StatusCode: HttpStatusCode code } => DescribeStatus(code),
            HttpRequestException { InnerException: AuthenticationException } or AuthenticationException =>
                "A secure connection could not be made. Check that your PC's date and time are correct and that no firewall or antivirus is blocking the app, then try again.",
            HttpRequestException or SocketException =>
                "Could not reach the internet. Check your connection or firewall, then try again.",
            TaskCanceledException or TimeoutException =>
                "The server took too long to answer. Check your connection and try again in a moment.",
            OperationCanceledException =>
                "It was cancelled before it finished, so nothing more was changed.",
            IOException io when io.HResult is DiskFullHResult or HandleDiskFullHResult =>
                "The drive is full. Free up some space, then try again.",
            IOException io when io.HResult == SharingViolationHResult =>
                "A file is in use by another program. Close that program, then try again.",
            UnauthorizedAccessException =>
                "Windows blocked access to a file or folder. Check that the folder is not read-only or protected, then try again.",
            IOException =>
                "A file could not be read or written. Check the drive and try again.",
            OutOfMemoryException =>
                "Your PC ran low on memory. Close other programs, then try again.",
            _ => "Something unexpected went wrong. Try again; if it keeps happening, use Show Logs to see the details."
        };
    }

    private static string DescribeStatus(HttpStatusCode code) => (int)code switch
    {
        401 or 403 => "The server refused the request. If you use a key, check it in Settings, then try again.",
        404 => "The file or page the app asked for is not there any more. Try again later.",
        408 => "The server took too long to answer. Try again in a moment.",
        429 => "Too many requests were sent in a short time. Wait a minute, then try again.",
        >= 500 => "The server is having trouble right now. Try again later.",
        _ => $"The server answered with an unexpected error ({(int)code}). Try again later."
    };

    private static Exception Unwrap(Exception ex)
    {
        Exception current = ex;
        while (current is AggregateException { InnerException: { } inner }) current = inner;
        if (current is HttpRequestException or UserFacingIOException or FreeVideoStudio.App.Services.AiProviderException
            or FreeVideoStudio.App.Services.AiMusicAutoFitException) return current;
        if (current.InnerException is UserFacingIOException or FreeVideoStudio.App.Services.AiProviderException) return current.InnerException;
        if (current.InnerException is HttpRequestException or SocketException or AuthenticationException or IOException)
            return current.InnerException;
        return current;
    }
}
