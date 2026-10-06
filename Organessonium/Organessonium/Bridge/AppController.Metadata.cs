using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private string PayloadMessage(EncryptedPayloadContext context, int count) =>
        PayloadReference(EncryptedPayloadProtocol.Message(context, count));

    private string PayloadReference(string content) =>
        new MetadataService(MetadataCodec.ForEncryption(_encryption, () => _store.Load().EncryptionMode))
            .EncodePayloadReference(content);
}
