// SSH フェンスの SFTP 操作（RemoteSession）を、手元のテスト用 SFTP サーバーに対して確認する。
// 使い方: dotnet run -- <host> <port> <user> <鍵ファイル> <作業用ローカルフォルダー>
using System.IO;
using Fako.Models;
using Fako.Remote;

// 子プロセスとして呼ばれたとき（ドラッグのテスト）
if (args[0] is "serve" or "drop")
{
    int code = 0;
    var th = new Thread(() =>
    {
        if (args[0] == "serve")
        {
            var c = new RemoteConfig { Host = args[1], Port = int.Parse(args[2]), User = args[3], Auth = RemoteAuth.KeyFile, KeyFile = args[4] };
            using var s = new RemoteSession(c, new Dictionary<string, string>()) { InvokeOnUi = f => f(), ConfirmHostKey = (_, _) => true };
            var h = s.ConnectAsync("").GetAwaiter().GetResult();
            DragOutTest.Serve(s, h, args[5], args[6], args[7]);
        }
        else code = DragOutTest.Drop(args[1], args[2], args[3]);
    });
    th.SetApartmentState(ApartmentState.STA);
    th.Start();
    th.Join();
    return code;
}

var (host, port, user, key, work) = (args[0], int.Parse(args[1]), args[2], args[3], args[4]);
if (Directory.Exists(work)) Directory.Delete(work, true);
Directory.CreateDirectory(work);
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}"); if (!ok) failures++; }

var cfg = new RemoteConfig { Host = host, Port = port, User = user, Auth = RemoteAuth.KeyFile, KeyFile = key };
var known = new Dictionary<string, string>();
using (var s = new RemoteSession(cfg, known) { InvokeOnUi = f => f(), ConfirmHostKey = (_, _) => true })
{
    var home = await s.ConnectAsync("");
    Check(home == "/home/tester", $"接続・ホーム = {home}");
    Check(known.ContainsKey($"{host}:{port}"), $"ホスト鍵を登録: {known.Values.FirstOrDefault()}");

    var list = await s.ListAsync(home);
    Check(list.Any(e => e.Name == "README.md" && !e.IsDirectory) && list.Any(e => e.Name == "logs" && e.IsDirectory), $"一覧 ({list.Count} 項目)");

    // アップロード（ファイル＋フォルダーごと）
    var up = Path.Combine(work, "up"); Directory.CreateDirectory(Path.Combine(up, "sub"));
    File.WriteAllText(Path.Combine(up, "a.txt"), "hello 日本語");
    File.WriteAllBytes(Path.Combine(up, "sub", "big.bin"), new byte[1_500_000]);
    var single = Path.Combine(work, "single.txt"); File.WriteAllText(single, "single");
    var progress = new List<string>();
    await s.UploadAsync(new[] { up, single }, home, new SyncProgress(progress.Add));
    list = await s.ListAsync(home);
    Check(list.Any(e => e.Name == "up" && e.IsDirectory) && list.Any(e => e.Name == "single.txt"), "アップロード（フォルダー＋ファイル）");
    var sub = await s.ListAsync(home + "/up/sub");
    Check(sub.Any(e => e.Name == "big.bin" && e.Length == 1_500_000), "フォルダーの中身もアップロード（1.5MB）");
    Check(progress.Any(p => p.Contains('%')), $"進み具合の通知 ({progress.Count} 回, 例: {progress.LastOrDefault()})");

    // 名前の変更・フォルダー作成・移動
    await s.RenameAsync(home + "/single.txt", home + "/renamed.txt");
    await s.CreateDirectoryAsync(home + "/newdir");
    await s.RenameAsync(home + "/renamed.txt", home + "/newdir/renamed.txt");
    var nd = await s.ListAsync(home + "/newdir");
    Check(nd.Any(e => e.Name == "renamed.txt"), "名前の変更・新しいフォルダー・フォルダーへの移動");

    // ダウンロード（フォルダーごと）と、編集用の 1 ファイル上書きダウンロード
    var down = Path.Combine(work, "down"); Directory.CreateDirectory(down);
    list = await s.ListAsync(home);
    var saved = await s.DownloadAsync(list.Where(e => e.Name is "up" or "README.md"), down);
    Check(File.Exists(Path.Combine(down, "up", "sub", "big.bin")) && new FileInfo(Path.Combine(down, "up", "sub", "big.bin")).Length == 1_500_000,
        "フォルダーごとダウンロード");
    Check(File.ReadAllText(Path.Combine(down, "up", "a.txt")) == "hello 日本語", "内容が一致（UTF-8）");
    var again = await s.DownloadAsync(list.Where(e => e.Name == "README.md"), down);
    Check(Path.GetFileName(again[0]) == "README (2).md", $"同名は上書きせず別名で保存: {Path.GetFileName(again[0])}");

    // 編集して書き戻す
    var edit = Path.Combine(work, "edit.md");
    await s.DownloadToAsync(list.First(e => e.Name == "README.md"), edit);
    File.AppendAllText(edit, "\n追記しました");
    await s.UploadToAsync(edit, home + "/README.md");
    var check = Path.Combine(work, "check.md");
    await s.DownloadToAsync((await s.ListAsync(home)).First(e => e.Name == "README.md"), check);
    Check(File.ReadAllText(check).EndsWith("追記しました"), "編集したファイルの書き戻し");

    // 削除（フォルダーは中身ごと）
    list = await s.ListAsync(home);
    await s.DeleteAsync(list.Where(e => e.Name is "up" or "newdir"));
    list = await s.ListAsync(home);
    Check(!list.Any(e => e.Name is "up" or "newdir"), "フォルダーを中身ごと削除");

    // 元の README に戻しておく
    File.WriteAllText(edit, "# README\nこれは SSH フェンスのテストです。\n");
    await s.UploadToAsync(edit, home + "/README.md");
}

// ホスト鍵が変わったら接続を拒否する
var tampered = new Dictionary<string, string> { [$"{host}:{port}"] = "ssh-rsa SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" };
using (var s = new RemoteSession(cfg, tampered) { InvokeOnUi = f => f(), ConfirmHostKey = (_, _) => true })
{
    try { await s.ConnectAsync(""); Check(false, "ホスト鍵の不一致で拒否"); }
    catch (HostKeyMismatchException) { Check(true, "ホスト鍵の不一致で拒否（なりすまし対策）"); }
}

// 初回のホスト鍵を「信頼しない」と答えたら接続しない
using (var s = new RemoteSession(cfg, new Dictionary<string, string>()) { InvokeOnUi = f => f(), ConfirmHostKey = (_, _) => false })
{
    try { await s.ConnectAsync(""); Check(false, "ホスト鍵を信頼しなければ中止"); }
    catch (HostKeyMismatchException ex) { Check(ex.Message.Contains("信頼しなかった"), "ホスト鍵を信頼しなければ中止"); }
}

// エクスプローラーへのドラッグ（仮想ファイル）: データを持つプロセス（A）と、ドロップするプロセス（B）に分ける
{
    var self = Environment.ProcessPath!;
    var objref = Path.Combine(work, "objref.txt");
    var done = Path.Combine(work, "done.txt");
    var log = Path.Combine(work, "serve.log");
    foreach (var f in new[] { objref, done, log }) File.Delete(f);
    using var serve = System.Diagnostics.Process.Start(self, new[] { "serve", host, port.ToString(), user, key, objref, done, log });
    for (int i = 0; i < 200 && !File.Exists(objref); i++) Thread.Sleep(100);
    var psi = new System.Diagnostics.ProcessStartInfo(self) { RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var a in new[] { "drop", objref, Path.Combine(work, "drop-target"), log }) psi.ArgumentList.Add(a);
    using var drop = System.Diagnostics.Process.Start(psi)!;
    Console.Write(drop.StandardOutput.ReadToEnd());
    drop.WaitForExit();
    File.WriteAllText(done, "done");
    serve.WaitForExit(10000);
    if (drop.ExitCode != 0) failures += drop.ExitCode;
}

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

sealed class SyncProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
