using System.Text.Json;
using System.Text.Json.Serialization;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Plc;
namespace Svm.ServiceDefaults;

public static class PlcConfiguration
{
    public static PlcOptions LoadFromEnvironment()
    {
        var path=Environment.GetEnvironmentVariable("SVM_PLC_CONFIG_FILE"); if(string.IsNullOrWhiteSpace(path))return new();
        try
        {
            var x=JsonSerializer.Deserialize<PlcOptions>(File.ReadAllText(path),new JsonSerializerOptions(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow});
            if(x?.UploadCredentialMaxLifetimeSeconds is not (>0 and <=31536000))throw new InvalidOperationException();return x;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);}
    }
}
