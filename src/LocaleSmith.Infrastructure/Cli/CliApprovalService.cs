using System.Collections.Concurrent;
using System.Security.Cryptography;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.Cli;

public sealed class CliApprovalService : ICliApprovalService
{
    private readonly ConcurrentDictionary<string, Approval> _approvals = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;

    public CliApprovalService(TimeProvider? timeProvider = null, TimeSpan? lifetime = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime ?? TimeSpan.FromMinutes(2);
        if (_lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Approval lifetime must be positive.");
        }
    }

    public string Issue(CliCommand command, bool userAcknowledgedRisk)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!userAcknowledgedRisk)
        {
            throw new InvalidOperationException("The user must acknowledge the CLI execution risk before approval.");
        }

        RemoveExpired();
        string token;
        do
        {
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        while (!_approvals.TryAdd(
                   token,
                   new Approval(command.ComputeFingerprint(), _timeProvider.GetUtcNow().Add(_lifetime))));

        return token;
    }

    public bool TryConsume(string token, CliCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(command);
        if (!_approvals.TryRemove(token, out var approval) || approval.ExpiresAt < _timeProvider.GetUtcNow())
        {
            return false;
        }

        var expected = Convert.FromHexString(approval.CommandFingerprint);
        var actual = Convert.FromHexString(command.ComputeFingerprint());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private void RemoveExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _approvals)
        {
            if (pair.Value.ExpiresAt < now)
            {
                _approvals.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record Approval(string CommandFingerprint, DateTimeOffset ExpiresAt);
}
