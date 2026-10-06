using System.IO;

namespace Organessonium.Services.MetadataEncryption;

public enum MetadataError
{
    InvalidFormat, UnsupportedVersion, InvalidBase64, KeyUnavailable, KeyMismatch,
    AuthenticationFailed, InvalidPlaintextJson, MissingField, TooLarge
}

public sealed class MetadataCodecException : IOException
{
    public MetadataError Error { get; }
    public MetadataCodecException(MetadataError error) : base(error switch
    {
        MetadataError.UnsupportedVersion => "O formato de metadados ainda não é suportado por esta versão.",
        MetadataError.KeyUnavailable => "A chave necessária para abrir os metadados não está disponível. Importe a chave de recuperação.",
        MetadataError.KeyMismatch => "Não foi possível descriptografar os metadados. A chave pode estar incorreta ou os dados adulterados.",
        MetadataError.AuthenticationFailed => "A autenticação dos metadados falhou. Os dados podem estar corrompidos ou adulterados.",
        MetadataError.TooLarge => "Os metadados excedem o limite de armazenamento criptografado.",
        _ => "O formato dos metadados é inválido ou incompleto. Os dados existentes foram preservados."
    }) => Error = error;
}
