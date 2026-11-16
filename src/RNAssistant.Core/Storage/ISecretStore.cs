namespace RNAssistant.Core.Storage
{
    public interface ISecretStore
    {
        string LoadSecret();
        void SaveSecret(string value);
    }
}
