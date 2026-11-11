using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoControls
{
    /// <summary>
    /// Native Windows "Open" dialog for picking the blank image. Runs on its own STA thread, so the plugin
    /// keeps handling Stream Dock events while it's open. The inspector can't do this itself: a browser file
    /// input doesn't reveal the file's real path.
    /// </summary>
    internal static class FileDialog
    {
        static int open; // 1 while a dialog is showing; a second Browse click is ignored

        /// <summary>Returns the picked path, or null if cancelled (or a dialog is already open).</summary>
        public static Task<string> PickImage(string current)
        {
            var done = new TaskCompletionSource<string>();
            if (Interlocked.Exchange(ref open, 1) == 1)
            {
                done.SetResult(null);
                return done.Task;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    // An invisible topmost owner, so the dialog opens in front of Stream Dock.
                    using (var owner = new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0,
                                                  StartPosition = FormStartPosition.CenterScreen })
                    using (var dialog = new OpenFileDialog())
                    {
                        dialog.Title = "Image to show while the camera is blank";
                        dialog.Filter = "Images and videos|*.png;*.jpg;*.jpeg;*.bmp;*.mp4;*.mov;*.avi;*.mkv;*.webm|" +
                                        "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*";
                        dialog.CheckFileExists = true;
                        dialog.Multiselect = false;
                        if (!string.IsNullOrEmpty(current))
                        {
                            try
                            {
                                string dir = Path.GetDirectoryName(current);
                                if (Directory.Exists(dir)) dialog.InitialDirectory = dir;
                                dialog.FileName = Path.GetFileName(current);
                            }
                            catch (ArgumentException) { }
                        }
                        owner.Show();
                        owner.Activate();
                        done.SetResult(dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null);
                    }
                }
                catch (Exception ex)
                {
                    StreamDock.Log("File dialog failed: " + ex.Message);
                    done.TrySetResult(null);
                }
                finally
                {
                    Interlocked.Exchange(ref open, 0);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return done.Task;
        }
    }
}
