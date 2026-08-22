// ZXing.Net на .NET Core отдаёт только generic BarcodeReader<T>: чтобы читать
// System.Drawing.Bitmap, нужен BarcodeReader из ZXing.Windows.Compatibility, а он
// лежит в другом namespace. Перенесённый Browser/HtmlExtensions.cs пишет
// `using ZXing; new BarcodeReader()` — так он написан под ZennoPoster, и трогать
// копию нельзя. Поэтому недостающее имя объявляется здесь, в namespace ZXing,
// ровно тем же переходником, каким у нас объявлен ZennoLab.CommandCenter.

using System.Drawing;

namespace ZXing
{
    /// <summary>Неродовой BarcodeReader эталона: читает штрихкод с Bitmap.</summary>
    public sealed class BarcodeReader
    {
        private readonly Windows.Compatibility.BarcodeReader _inner = new();

        public Result Decode(Bitmap bitmap) => bitmap is null ? null : _inner.Decode(bitmap);
    }
}
