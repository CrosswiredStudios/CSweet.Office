using System.Security.Cryptography.X509Certificates;

namespace CSweet.Office.Node;

/// <summary>Keeps retired credentials alive past the handler's bounded handshake timeout.</summary>
internal sealed class OfficeCertificateLease : IDisposable
{
    private readonly X509Certificate2 _initial;
    private X509Certificate2 _current;
    public OfficeCertificateLease(X509Certificate2 initial) => _initial = _current = initial;
    private readonly List<(X509Certificate2 Certificate, long RetiredAt)> _retired = [];
    public X509Certificate2 Current => Volatile.Read(ref _current);
    public void Update(X509Certificate2 certificate)
    {
        // Renewal is single-writer. A retired certificate is never selected for a new
        // handshake; two minutes exceeds the rotating handler's 20-second connect timeout.
        foreach (var retired in _retired.Where(x =>
                     TimeProvider.System.GetElapsedTime(x.RetiredAt) >= TimeSpan.FromMinutes(2)).ToArray())
        {
            retired.Certificate.Dispose();
            _retired.Remove(retired);
        }
        var previous = Current;
        Volatile.Write(ref _current, certificate);
        if (!ReferenceEquals(previous, _initial))
            _retired.Add((previous, TimeProvider.System.GetTimestamp()));
    }
    public void Dispose()
    {
        if (!ReferenceEquals(Current, _initial)) Current.Dispose();
        foreach (var retired in _retired) retired.Certificate.Dispose();
    }
}
