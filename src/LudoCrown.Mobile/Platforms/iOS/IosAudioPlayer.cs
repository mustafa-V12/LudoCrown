using AVFoundation;
using Foundation;

namespace LudoCrown.Mobile.Services;

/// <summary>Plays bundled WAV effects with AVAudioPlayer, mixing politely with other audio (respects the silent switch).</summary>
public sealed class IosAudioPlayer : IAudioPlayer
{
    private readonly Dictionary<string, AVAudioPlayer> _players = new();

    public IosAudioPlayer()
    {
        AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Ambient);
    }

    public void Play(string fileName)
    {
        try
        {
            if (!_players.TryGetValue(fileName, out var player))
            {
                var path = NSBundle.MainBundle.PathForResource(Path.GetFileNameWithoutExtension(fileName), Path.GetExtension(fileName).TrimStart('.'));
                if (path is null) return;
                player = AVAudioPlayer.FromUrl(NSUrl.FromFilename(path));
                if (player is null) return;
                player.PrepareToPlay();
                _players[fileName] = player;
            }
            player.CurrentTime = 0;
            player.Play();
        }
        catch (Exception)
        {
            // Sound is decorative; never crash the game over it.
        }
    }
}
