using MapleLedger.Core.Receipts;

namespace MapleLedger.UnitTests;

public class ReceiptRelayTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly RelayedReceipt Photo = new("IMG_1.jpg", "image/jpeg", [0xFF, 0xD8, 0xFF, 0x01]);

    [Fact]
    public void APhotoIsHandedToTheComputerOnceAndThenForgotten()
    {
        var store = new ReceiptRelayStore(new Clock());
        var ticket = store.Create()!;

        Assert.Equal(ReceiptRelayStore.PickupState.Waiting, store.TryPickUp(ticket.PickupToken, out _));
        Assert.True(store.IsOpen(ticket.UploadToken));

        Assert.Equal(ReceiptRelayStore.DepositResult.Accepted, store.Deposit(ticket.UploadToken, Photo));
        Assert.False(store.IsOpen(ticket.UploadToken));
        Assert.Equal(ReceiptRelayStore.DepositResult.AlreadyUsed, store.Deposit(ticket.UploadToken, Photo));

        Assert.Equal(ReceiptRelayStore.PickupState.Ready, store.TryPickUp(ticket.PickupToken, out var file));
        Assert.Same(Photo, file);
        Assert.Equal(ReceiptRelayStore.PickupState.Gone, store.TryPickUp(ticket.PickupToken, out _));
        Assert.Equal(0, store.OpenSessions);
    }

    [Fact]
    public void ThePhonesTokenCantCollectAndTheComputersCantUpload()
    {
        var store = new ReceiptRelayStore(new Clock());
        var ticket = store.Create()!;

        Assert.NotEqual(ticket.UploadToken, ticket.PickupToken);
        Assert.Equal(ReceiptRelayStore.PickupState.Gone, store.TryPickUp(ticket.UploadToken, out _));
        Assert.Equal(ReceiptRelayStore.DepositResult.Gone, store.Deposit(ticket.PickupToken, Photo));
        Assert.Matches("^[A-Za-z0-9_-]{22}$", ticket.UploadToken);
    }

    [Fact]
    public void SessionsExpireAndAreCapped()
    {
        var clock = new Clock();
        var store = new ReceiptRelayStore(clock, TimeSpan.FromMinutes(10), maxSessions: 2);
        var first = store.Create()!;
        Assert.NotNull(store.Create());
        Assert.Null(store.Create()); // full

        clock.Now += TimeSpan.FromMinutes(10);

        Assert.False(store.IsOpen(first.UploadToken));
        Assert.Equal(ReceiptRelayStore.DepositResult.Gone, store.Deposit(first.UploadToken, Photo));
        Assert.Equal(ReceiptRelayStore.PickupState.Gone, store.TryPickUp(first.PickupToken, out _));
        Assert.NotNull(store.Create()); // expired ones no longer count
    }

    [Fact]
    public void CancellingEndsTheSession()
    {
        var store = new ReceiptRelayStore(new Clock());
        var ticket = store.Create()!;

        store.Cancel(ticket.PickupToken);

        Assert.False(store.IsOpen(ticket.UploadToken));
    }

    [Theory]
    // A configured address always wins.
    [InlineData("http://192.168.1.20:7080/", "https", "localhost", 7022, "192.168.1.5", 7099, "http://192.168.1.20:7080")]
    // Opened by the tunnel hostname or a network address: phones use that too.
    [InlineData(null, "https", "ledger.example.com", null, "192.168.1.5", 7099, "https://ledger.example.com")]
    [InlineData(null, "http", "192.168.1.5", 7080, null, null, "http://192.168.1.5:7080")]
    // Opened by localhost: this computer's network address on the phone port.
    [InlineData(null, "https", "localhost", 7022, "192.168.1.5", 7099, "http://192.168.1.5:7099")]
    [InlineData(null, "https", "127.0.0.1", 7022, "10.0.0.8", 7099, "http://10.0.0.8:7099")]
    // Localhost with no network address or phone port: nothing a phone can reach.
    [InlineData(null, "https", "localhost", 7022, null, 7099, null)]
    [InlineData(null, "https", "localhost", 7022, "192.168.1.5", null, null)]
    public void PicksAnAddressThePhoneCanReach(string? configured, string scheme, string host, int? port, string? lan, int? lanPort, string? expected) =>
        Assert.Equal(expected, ReceiptRelayStore.PhoneBaseUrl(configured, scheme, host, port, lan, lanPort));
}
