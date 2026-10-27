using System.Security.Cryptography;
using Wisp.Core;

var root = Path.Combine(Path.GetTempPath(), "wisp-cs-" + Guid.NewGuid().ToString("N")[..8]);
var sendRoot = Path.Combine(root, "send");
var receiveRoot = Path.Combine(root, "receive");
Directory.CreateDirectory(sendRoot);
Directory.CreateDirectory(receiveRoot);

var source = Path.Combine(sendRoot, "payload.bin");
var data = RandomNumberGenerator.GetBytes(8 * 1024 * 1024 + 777);
await File.WriteAllBytesAsync(source, data);

using var receiver = new WispEngine();
using var sender = new WispEngine();
var senderCompletion = new TaskCompletionSource<TransferCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
var senderProgressEvents = 0;
var receiverProgressEvents = 0;

receiver.TransferCompleted += (_, completion) =>
{
    Console.WriteLine($"receiver complete: {completion.TransferId} -> {completion.SavedPath}");
};
receiver.ProgressChanged += (_, progress) =>
{
    if (progress.BytesDone > 0 && progress.BytesDone < progress.BytesTotal)
        Interlocked.Increment(ref receiverProgressEvents);
};
receiver.TransferFailed += (_, failure) =>
{
    Console.WriteLine($"receiver failed: {failure.Message}");
};
receiver.OfferReceived += (_, offer) =>
{
    Console.WriteLine($"receiver offer: {offer.PeerName} {offer.FileCount} file(s)");
};
sender.TransferCompleted += (_, completion) =>
{
    Console.WriteLine($"sender complete: {completion.TransferId}");
    senderCompletion.TrySetResult(completion);
};
sender.ProgressChanged += (_, progress) =>
{
    if (progress.BytesDone > 0 && progress.BytesDone < progress.BytesTotal)
        Interlocked.Increment(ref senderProgressEvents);
};
sender.TransferFailed += (_, failure) =>
{
    Console.WriteLine($"sender failed: {failure.Message}");
    senderCompletion.TrySetException(new InvalidOperationException(failure.Message));
};
sender.TransferCancelled += (_, failure) =>
{
    senderCompletion.TrySetCanceled();
};

receiver.Start(new WispConfig
{
    Name = "C# Receiver",
    SaveDirectory = receiveRoot,
    IdentityDirectory = Path.Combine(root, "identity-receiver"),
    DiscoveryPort = 0,
    AutoAccept = true
});
sender.Start(new WispConfig
{
    Name = "C# Sender",
    SaveDirectory = Path.Combine(root, "sender-downloads"),
    IdentityDirectory = Path.Combine(root, "identity-sender"),
    DiscoveryPort = 0
});

var receiverPeer = receiver.Self ?? throw new InvalidOperationException("Receiver has no identity.");
const string linkToken = "0123456789abcdef0123456789abcdef";
var qr = QrLinkPayload.Create(
    "Wisp & Phone", "pass phrase+&=/%20", "192.168.43.1", 54321,
    receiverPeer.Fingerprint, linkToken, "Phone 😊", TimeSpan.FromMinutes(2));
if (!qr.TryValidate(out var qrError))
    throw new InvalidOperationException($"Generated QR payload is invalid: {qrError}");
if (!QrLinkPayload.TryParse(qr.ToUriString(), out var parsedQr, out var parseError) ||
    parsedQr is null)
    throw new InvalidOperationException($"Could not parse the generated QR payload: {parseError}");
if (parsedQr.Ssid != qr.Ssid || parsedQr.Passphrase != qr.Passphrase ||
    parsedQr.Host != qr.Host || parsedQr.Port != qr.Port ||
    parsedQr.Fingerprint != qr.Fingerprint || parsedQr.Token != qr.Token ||
    parsedQr.DeviceName != qr.DeviceName)
    throw new InvalidOperationException("QR payload fields did not round-trip.");
Console.WriteLine($"QR payload round-trip: {parsedQr.EndpointText}, {parsedQr.DeviceName}");
if (!receiver.SetExpectedLinkToken(linkToken))
    throw new InvalidOperationException("Could not set the expected link token.");
if (!sender.SetLinkToken(linkToken))
    throw new InvalidOperationException("Could not set the sender link token.");
if (!sender.SetBindAddress("127.0.0.1"))
    throw new InvalidOperationException("Could not bind the sender source address.");
if (!sender.AddPeer(receiverPeer with { Host = "127.0.0.1" }))
    throw new InvalidOperationException("Could not add the receiver as a peer.");

var started = DateTime.UtcNow;
var transferId = sender.Send([source], receiverPeer.Id);
Console.WriteLine($"started transfer {transferId}");
var completion = await senderCompletion.Task.WaitAsync(TimeSpan.FromSeconds(60));
var elapsed = DateTime.UtcNow - started;

if (senderProgressEvents == 0 || receiverProgressEvents == 0)
    throw new InvalidOperationException(
        $"Intermediate progress was missing (sender={senderProgressEvents}, receiver={receiverProgressEvents}).");
Console.WriteLine($"progress events: sender={senderProgressEvents}, receiver={receiverProgressEvents}");

var received = Path.Combine(receiveRoot, "payload.bin");
if (!File.Exists(received)) throw new InvalidOperationException("Received file is missing.");
var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
var receivedHash = SHA256.HashData(await File.ReadAllBytesAsync(received));
if (!sourceHash.SequenceEqual(receivedHash))
    throw new InvalidOperationException("Received file hash does not match.");

Console.WriteLine($"verified {completion.BytesTotal:N0} bytes in {elapsed.TotalSeconds:0.00}s " +
                  $"({completion.BytesTotal / 1024.0 / 1024.0 / elapsed.TotalSeconds:0.0} MiB/s)");
Directory.Delete(root, true);
return 0;
