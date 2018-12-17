using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Input;
using System.Windows.Interop;
using CefSharp.Structs;
using CefSharp.Wpf.Internals;
using CefSharp;

namespace CefSharp.Wpf.IME
{
    public class IMEWpfKeyboardHandler : WpfKeyboardHandler
    {
        static bool _subclassed;
        int _languageCodeId;
        bool _systemCaret;
        bool _isDisposed = false;
        int _cursorIndex;
        Range _compositionRange;
        Rect _imeRect = new Rect(-1, -1, 0, 0);
        List<Rect> _compositionBounds = new List<Rect>();
        HwndSource _source;
        IntPtr _defaultContext;
        IntPtr _browserContext;
        Func<IntPtr, int> _loWord;

        internal bool IsActive { get; set; }

        /// <summary>
        /// The source hook
        /// </summary>
        private HwndSourceHook sourceHook;

        public IMEWpfKeyboardHandler(ChromiumWebBrowser owner) : base(owner)
        {
            if (IntPtr.Size == sizeof(Int32))
                _loWord = x => x.ToInt32();
            else
                _loWord = LOWORD64;
        }

        private void Owner_LostFocus(object sender, System.Windows.RoutedEventArgs e)
        {
            IsActive = false;
            InputMethod.SetIsInputMethodEnabled(owner, false);
            InputMethod.SetIsInputMethodSuspended(owner, false);
        }

        private void Owner_GotFocus(object sender, System.Windows.RoutedEventArgs e)
        {
            InputMethod.SetIsInputMethodEnabled(owner, true);
            InputMethod.SetIsInputMethodSuspended(owner, true);
            IsActive = true;
        }

        public override void Setup(HwndSource source)
        {
            if (!_subclassed)
            {
                _source = source;
                sourceHook = SourceHook;
                source.AddHook(SourceHook);
            }

            _subclassed = true;

            owner.GotFocus += Owner_GotFocus;
            owner.LostFocus += Owner_LostFocus;

/*
            _defaultContext = NativeIME.ImmGetContext(_source.Handle);
            _browserContext = NativeIME.ImmCreateContext();

            NativeIME.ImmAssociateContext(_source.Handle, _browserContext);
*/

            // TODO: need to find a better way to trigger setting context on the window
            owner.Focus();
        }

        public override void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            owner.GotFocus -= Owner_GotFocus;
            owner.LostFocus -= Owner_LostFocus;

/*
            if (_source != null)
            {
                NativeIME.ImmAssociateContext(_source.Handle, _defaultContext);
            }
            NativeIME.ImmDestroyContext(_browserContext);
*/

            if (_source != null && sourceHook != null)
            {
                _source.RemoveHook(sourceHook);
                _source = null;
            }
        }

        private IntPtr SourceHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (owner == null || owner.GetBrowserHost() == null || owner.IsDisposed || !IsActive || _isDisposed)
                return IntPtr.Zero;

            handled = false;

            Console.WriteLine(msg);
            switch (msg)
            {
                case NativeIME.WM_SIZE:
                    if (wParam.ToInt32() == NativeIME.SIZE_RESTORED || wParam.ToInt32() == NativeIME.SIZE_MAXIMIZED)
                    {
                        Console.WriteLine("!!!!!!!!!!!!!!!!!!");
                        NativeIME.SetFocus(IntPtr.Zero);
                        NativeIME.SetFocus(hwnd);
                    }
                    break;

                case NativeIME.WM_IME_SETCONTEXT:
                    OnIMESetContext(hwnd, (uint) msg, wParam, lParam);
                    handled = true;
                    break;

                case NativeIME.WM_IME_STARTCOMPOSITION:
                    OnIMEStartComposition(hwnd);
                    handled = true;
                    break;

                case NativeIME.WM_IME_COMPOSITION:
                    OnIMEComposition(hwnd, _loWord(lParam));
                    handled = true;
                    break;

                case NativeIME.WM_IME_ENDCOMPOSITION:
                    OnIMEEndComposition(hwnd);
                    handled = true;
                    break;
            }

            return handled ? IntPtr.Zero : new IntPtr(1);
        }

        private void OnIMEComposition(IntPtr hwnd, int lParam)
        {
            string text = string.Empty;

            var handler = IMEHandler.Create(hwnd);

            if (handler.GetResult((uint)lParam, out text))
            {
                owner.GetBrowserHost().ImeCommitText(text);

                ResetComposition();
            }
            else
            {
                var underlines = new List<CompositionUnderline>();
                int compositionStart = 0;

                if (handler.GetComposition((uint)lParam, underlines, ref compositionStart, out text))
                {
                    owner.GetBrowserHost().ImeSetComposition(text, underlines.ToArray(), new Range(compositionStart, compositionStart));

                    UpdateCaretPosition(compositionStart - 1);
                }
                else
                {
                    CancelComposition(hwnd);
                }
            }
        }

        public void CancelComposition(IntPtr hwnd)
        {
            owner.GetBrowserHost().ImeCancelComposition();
            ResetComposition();
            DestroyImeWindow(hwnd);
        }

        private void OnIMEEndComposition(IntPtr hwnd)
        {
            owner.GetBrowserHost().ImeFinishComposingText(false);
            ResetComposition();
            DestroyImeWindow(hwnd);
        }

        private void OnIMESetContext(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // We handle the IME Composition Window ourselves (but let the IME Candidates
            // Window be handled by IME through DefWindowProc()), so clear the
            // ISC_SHOWUICOMPOSITIONWINDOW flag:
            lParam = (IntPtr)(lParam.ToInt64() & ~NativeIME.ISC_SHOWUICOMPOSITIONWINDOW);
            NativeIME.DefWindowProc(hwnd, msg, wParam, lParam);
            // TODO: should we call ImmNotifyIME?

            CreateImeWindow(hwnd);
            MoveImeWindow(hwnd);
        }

        private void OnIMEStartComposition(IntPtr hwnd)
        {
            CreateImeWindow(hwnd);
            MoveImeWindow(hwnd);
            ResetComposition();
        }

        private void ResetComposition()
        {
            _cursorIndex = -1;
        }

        private void CreateImeWindow(IntPtr hwnd)
        {
            // Chinese/Japanese IMEs somehow ignore function calls to
            // ::ImmSetCandidateWindow(), and use the position of the current system
            // caret instead -::GetCaretPos().
            // Therefore, we create a temporary system caret for Chinese IMEs and use
            // it during this input context.
            // Since some third-party Japanese IME also uses ::GetCaretPos() to determine
            // their window position, we also create a caret for Japanese IMEs.
            _languageCodeId = PrimaryLangId(InputLanguageManager.Current.CurrentInputLanguage.KeyboardLayoutId);

            if (_languageCodeId == NativeIME.LANG_JAPANESE || _languageCodeId == NativeIME.LANG_CHINESE)
            {
                if (!_systemCaret)
                {
                    if (NativeIME.CreateCaret(hwnd, IntPtr.Zero, 1, 1))
                    {
                        _systemCaret = true;
                    }
                }
            }
        }

        private int PrimaryLangId(int lgid)
        {
            return (lgid & 0x3ff);
        }


        private void MoveImeWindow(IntPtr hwnd)
        {
            IntPtr hIMC = NativeIME.ImmGetContext(hwnd);

            Rect rc = _imeRect;
            int location = _cursorIndex;

            // If location is not specified fall back to the composition range start.
            if (location == -1)
            {
                location = _compositionRange.From;
            }

            // Offset location by the composition range start if required.
            if (location >= _compositionRange.From)
            {
                location -= _compositionRange.From;
            }

            if (location >= _compositionBounds.Count)
            {
                NativeIME.ImmReleaseContext(hwnd, hIMC);
                return;
            }

            rc = _compositionBounds[0];

            NativeIME.RECT rect;
            NativeIME.GetWindowRect(hwnd, out rect);

            int x = rc.X + rc.Width;
            int y = rc.Y + rc.Height;

            const int kCaretMargin = 1;
            // As written in a comment in ImeInput::CreateImeWindow(),
            // Chinese IMEs ignore function calls to ::ImmSetCandidateWindow()
            // when a user disables TSF (Text Service Framework) and CUAS (Cicero
            // Unaware Application Support).
            // On the other hand, when a user enables TSF and CUAS, Chinese IMEs
            // ignore the position of the current system caret and uses the
            // parameters given to ::ImmSetCandidateWindow() with its 'dwStyle'
            // parameter CFS_CANDIDATEPOS.
            // Therefore, we do not only call ::ImmSetCandidateWindow() but also
            // set the positions of the temporary system caret if it exists.
            var candidatePosition = new NativeIME.CANDIDATEFORM {
                dwIndex = 0,
                dwStyle = (int)NativeIME.CFS_CANDIDATEPOS,
                ptCurrentPos = new NativeIME.POINT(x, y),
                rcArea = new NativeIME.RECT(0, 0, 0, 0)
            };
            NativeIME.ImmSetCandidateWindow(hIMC, ref candidatePosition);

            if (_systemCaret)
            {
                NativeIME.SetCaretPos(x, y);
            }

            if (_languageCodeId == NativeIME.LANG_KOREAN)
            {
                // Chinese IMEs and Japanese IMEs require the upper-left corner of
                // the caret to move the position of their candidate windows.
                // On the other hand, Korean IMEs require the lower-left corner of the
                // caret to move their candidate windows.
                y += kCaretMargin;
            }
            // Japanese IMEs and Korean IMEs also use the rectangle given to
            // ::ImmSetCandidateWindow() with its 'dwStyle' parameter CFS_EXCLUDE
            // to move their candidate windows when a user disables TSF and CUAS.
            // Therefore, we also set this parameter here.
            var excludeRectangle = new NativeIME.CANDIDATEFORM
            {
                dwIndex = 0,
                dwStyle = (int)NativeIME.CFS_EXCLUDE,
                ptCurrentPos = new NativeIME.POINT(x, y),
                rcArea = new NativeIME.RECT(rc.X, rc.Y, x, y + kCaretMargin)
            };
            NativeIME.ImmSetCandidateWindow(hIMC, ref excludeRectangle);

            NativeIME.ImmReleaseContext(hwnd, hIMC);
        }

        private void DestroyImeWindow(IntPtr hwnd)
        {
            if (_systemCaret)
            {
                NativeIME.DestroyCaret();
                _systemCaret = false;
            }
        }

        internal void ChangeCompositionRange(Range selectionRange, List<Rect> bounds)
        {
            _compositionRange = selectionRange;
            _compositionBounds = bounds;
            MoveImeWindow(_source.Handle);
        }

        private void UpdateCaretPosition(int index)
        {
            _cursorIndex = index;
            MoveImeWindow(_source.Handle);
        }

        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        private static Int32 HIWORD64(IntPtr ptr)
        {
            return (Int32)((ptr.ToInt64() >> 16) & 0xFFFFFFFF);
        }

        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        private static Int32 LOWORD64(IntPtr ptr)
        {
            return (Int32)(ptr.ToInt64() & 0xFFFFFFFF);
        }
    }
}
