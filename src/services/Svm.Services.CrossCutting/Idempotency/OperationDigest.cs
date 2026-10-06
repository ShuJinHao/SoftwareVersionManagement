using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Svm.Services.Contracts.Framework;

namespace Svm.Services.CrossCutting.Idempotency;

internal static class OperationDigest
{
    internal static string Create(RequestPolicy policy, AuthorizationTarget target, OperationRequestData data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Text("SVM-semantic-operation-v1");
        Text(policy.Owner.ToString()); Text(policy.Operation); Text(target.Scope.ToString());
        Text(target.SoftwareId?.ToString("N") ?? ""); Text(target.InstanceId?.ToString("N") ?? "");
        Text(target.Owner?.ToString() ?? ""); Text(target.WorkId?.ToString("N") ?? "");
        Write(data.Route, 0); Write(data.Body, 0);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Text(string value)
        {
            byte[] bytes;
            try { bytes = new UTF8Encoding(false, true).GetBytes(value); }
            catch (EncoderFallbackException) { throw new RequestRejectedException(RequestFailure.InvalidRequest); }
            Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length);
            hash.AppendData(size); hash.AppendData(bytes);
        }
        void Write(OperationValue value, int depth)
        {
            if (depth > 64) throw new RequestRejectedException(RequestFailure.InvalidRequest);
            hash.AppendData([(byte)value.Kind]);
            switch (value.Kind)
            {
                case OperationValue.ValueKind.Null: break;
                case OperationValue.ValueKind.Text: Text((string)value.Value!); break;
                case OperationValue.ValueKind.Integer: Text(((long)value.Value!).ToString(CultureInfo.InvariantCulture)); break;
                case OperationValue.ValueKind.Number: Text(((decimal)value.Value!).ToString("G29", CultureInfo.InvariantCulture)); break;
                case OperationValue.ValueKind.Boolean: hash.AppendData([(bool)value.Value! ? (byte)1 : (byte)0]); break;
                case OperationValue.ValueKind.Identifier: Text(((Guid)value.Value!).ToString("N")); break;
                case OperationValue.ValueKind.Timestamp: Text(((DateTimeOffset)value.Value!).UtcTicks.ToString(CultureInfo.InvariantCulture)); break;
                case OperationValue.ValueKind.Object:
                    var fields = (IReadOnlyList<OperationField>)value.Value!;
                    Text(fields.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (var field in fields.OrderBy(f => f.Name, StringComparer.Ordinal)) { Text(field.Name); Write(field.Value, depth + 1); }
                    break;
                case OperationValue.ValueKind.Array:
                    var items = (IReadOnlyList<OperationValue>)value.Value!;
                    Text(items.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (var item in items) Write(item, depth + 1);
                    break;
                default: throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
            }
        }
    }
}
