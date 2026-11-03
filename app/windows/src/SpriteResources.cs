using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace CrossPet
{
    /// <summary>立繪（https://crosspet.data/characters/…）由程式自己讀檔案交給 WebView2，不用「虛擬域名 → 資料夾」對映。
    /// 為什麼：有的使用者那裡，WebView2 把資料目錄（%LOCALAPPDATA%\CrossPet）對映成虛擬域名時請求整個失敗——
    /// 1.3.3 立繪全是破圖，1.3.4 測試版 1 把網頁也挪到資料目錄後連網頁都打不開；安裝目錄的對映一直正常。
    /// 根本原因沒查清（資料目錄裡有 WebView2 自己的瀏覽器資料？新版 WebView2 的限制？），換成自己讀檔案後，
    /// 報告問題的使用者那裡 358 張立繪全部正常。這個辦法是那位使用者排查並實測的，感謝。
    /// 注意：同一個域名不能再做資料夾對映，否則對映優先、這裡收不到請求</summary>
    static class SpriteResources
    {
        public static void Attach(CoreWebView2 core)
        {
            core.AddWebResourceRequestedFilter($"https://{Store.DataHost}/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += async (_, e) =>
            {
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != Store.DataHost || !uri.IsDefaultPort) return;
                // 啟動時網頁會預載入全部立繪（幾百張）：檔案放到後臺讀，介面執行緒不卡；讀完回到介面執行緒再答覆（WebView2 的物件只能在介面執行緒用）
                var deferral = e.GetDeferral();
                try
                {
                    if (e.Request.Method != "GET" && e.Request.Method != "HEAD") { e.Response = Reply(core, 405, "Method Not Allowed"); return; }
                    if (!SpritePath.TryResolve(Store.Characters, uri.AbsolutePath, out var file, out var mime)) { e.Response = Reply(core, 403, "Forbidden"); return; }
                    if (!File.Exists(file)) { e.Response = Reply(core, 404, "Not Found"); return; }
                    // 整個檔案讀進記憶體再交出去：WebView2 在這個回撥結束後才非同步讀流，不能交一個馬上要關掉的檔案流
                    var bytes = await Task.Run(() => File.ReadAllBytes(file));
                    var body = e.Request.Method == "HEAD" ? new byte[0] : bytes;
                    e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(body, false), 200, "OK",
                        Headers + $"Content-Type: {mime}\r\nContent-Length: {bytes.Length}\r\n");
                }
                catch (Exception ex)
                {
                    // 讀圖時視窗被關掉（比如設定視窗）、WebView2 已經沒了：答覆也會出錯。這是 async void，漏出去的異常會讓整個程式崩掉，全部接住
                    Store.Log("讀立繪失敗: " + uri.AbsolutePath + " — " + ex.Message);
                    try { e.Response = Reply(core, 500, "Read Failed"); } catch { }
                }
                finally { try { deferral.Complete(); } catch { } }
            };
        }

        // 網頁在另一個域名（crosspet.app）：允許它讀；立繪可能被使用者換掉，不快取
        static readonly string Headers = $"Access-Control-Allow-Origin: https://{Store.AppHost}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n";

        static CoreWebView2WebResourceResponse Reply(CoreWebView2 core, int status, string reason) =>
            core.Environment.CreateWebResourceResponse(new MemoryStream(new byte[0], false), status, reason,
                Headers + "Content-Type: text/plain; charset=utf-8\r\nContent-Length: 0\r\n");
    }
}
