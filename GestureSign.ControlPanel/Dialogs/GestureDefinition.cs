﻿using System;
using GestureSign.Common.Gestures;
using GestureSign.Common.InterProcessCommunication;
using GestureSign.Common.Localization;
using GestureSign.ControlPanel.Common;
using MahApps.Metro.Controls;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GestureSign.Common.Applications;
using GestureSign.Common.Configuration;
using GestureSign.ControlPanel.ViewModel;
using ManagedWinapi;
using ManagedWinapi.Hooks;

namespace GestureSign.ControlPanel.Dialogs
{
    /// <summary>
    /// GestureDefinition.xaml 的交互逻辑
    /// </summary>
    public partial class GestureDefinition : MetroWindow
    {
        /// <summary>
        /// Select Gesture
        /// </summary>
        public GestureDefinition()
        {
            InitializeComponent();
            MessageProcessor.GotNewGesture += MessageProcessor_GotNewGesture;
        }

        public GestureDefinition(IGesture gesture)
            : this()
        {
            _oldGesture = (Gesture)gesture;
            _currentPointPatterns = gesture.PointPatterns;
            GestureManager.Instance.GestureName = gesture.Name;
        }

        private Gesture _oldGesture;
        private Gesture _similarGesture;
        private PointPattern[] _currentPointPatterns;
        private Color _color;

        public Gesture SimilarGesture
        {
            get { return _similarGesture; }
            set
            {
                _similarGesture = value;
                if (value == null)
                {
                    imgGestureThumbnail.Source = GestureImage.CreateImage(_currentPointPatterns, new Size(65, 65), _color);
                    cmdDone.Content = LocalizationProvider.Instance.GetTextValue("Common.Save");
                    ExistingTextBlock.Visibility = Visibility.Collapsed;
                }
                else
                {
                    cmdDone.Content = LocalizationProvider.Instance.GetTextValue("Common.OK");

                    if (_oldGesture?.Name == value.Name)
                        imgGestureThumbnail.Source = GestureImage.CreateImage(_currentPointPatterns, new Size(65, 65), _color);
                    else
                    {
                        ExistingTextBlock.Visibility = Visibility.Visible;
                        imgGestureThumbnail.Source = GestureImage.CreateImage(value.PointPatterns, new Size(65, 65), _color);
                    }
                    var hotkey = value.Hotkey;
                    if (hotkey != null)
                        HotKeyTextBox.HotKey = new HotKey(KeyInterop.KeyFromVirtualKey(hotkey.KeyCode), (ModifierKeys)hotkey.ModifierKeys);
                    MouseActionComboBox.SelectedValue = value.MouseAction;

                }
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (MouseActionDescription.DescriptionDict.ContainsKey(AppConfig.DrawingButton))
                DrawingButtonTextBlock.Text = MouseActionDescription.DescriptionDict[AppConfig.DrawingButton] + "  +  ";
            MouseNotEnabledTextBlock.Visibility = AppConfig.DrawingButton == MouseActions.None ? Visibility.Visible : Visibility.Collapsed;

            _color = (Color)Application.Current.Resources["HighlightColor"];

            if (_currentPointPatterns != null)
                imgGestureThumbnail.Source = GestureImage.CreateImage(_currentPointPatterns, new Size(65, 65), _color);

            if (_oldGesture == null)
            {
                await SetTrainingState(true);
                Title = LocalizationProvider.Instance.GetTextValue("GestureDefinition.SelectGesture");
            }
            else
            {
                MouseActionComboBox.SelectedValue = _oldGesture.MouseAction;
                Title = LocalizationProvider.Instance.GetTextValue("GestureDefinition.Edit");
                var hotkey = ((Gesture)_oldGesture).Hotkey;
                if (hotkey != null)
                    HotKeyTextBox.HotKey = new HotKey(KeyInterop.KeyFromVirtualKey(hotkey.KeyCode), (ModifierKeys)hotkey.ModifierKeys);
            }
        }


        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            await NamedPipe.SendMessageAsync("StopTraining", "GestureSignDaemon");
        }

        private void cmdDone_Click(object sender, RoutedEventArgs e)
        {
            string gestureName;
            PointPattern[] newPatterns;
            if (_oldGesture != null)
            {
                //merge similar gesture
                if (_similarGesture != null)
                {
                    if (_oldGesture.Name == _similarGesture.Name)
                    {
                        newPatterns = _currentPointPatterns;
                    }
                    else
                    {
                        GestureManager.Instance.DeleteGesture(_similarGesture.Name);
                        ApplicationManager.Instance.RenameGesture(_oldGesture.Name, _similarGesture.Name);
                        newPatterns = _similarGesture.PointPatterns;
                    }
                }
                else
                {
                    newPatterns = _currentPointPatterns;
                }
                gestureName = _oldGesture.Name;
            }
            else
            {
                if (_similarGesture != null)
                {
                    gestureName = _similarGesture.Name;
                    newPatterns = _similarGesture.PointPatterns;
                }
                else
                {
                    newPatterns = _currentPointPatterns;
                    gestureName = null;
                }
            }

            if (newPatterns == null || newPatterns.Length == 0)
            {
                GestureTabControl.SelectedIndex = 0;
                return;
            }

            if (SaveGesture(gestureName, newPatterns))
            {
                DialogResult = true;
                Close();
            }
        }

        private async void MessageProcessor_GotNewGesture(object sender, Gesture e)
        {
            _currentPointPatterns = e.PointPatterns;
            SimilarGesture = (Gesture)GestureManager.Instance.GetNewestGestureSample(e.Name);

            await SetTrainingState(false);
        }

        private async void RedrawButton_Click(object sender, RoutedEventArgs e)
        {
            await SetTrainingState(true);
        }

        private async void imgGestureThumbnail_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && _oldGesture == null && _currentPointPatterns?.Length < 3)
            {
                await NamedPipe.SendMessageAsync("StackUpGesture", "GestureSignDaemon");
                await SetTrainingState(true);
            }
        }

        #region Private Methods

        private async Task<bool> SetTrainingState(bool state)
        {
            if (state)
            {
                SimilarGesture = null;
                _currentPointPatterns = null;
                imgGestureThumbnail.Source = null;
                MouseActionComboBox.SelectedItem = null;
                HotKeyTextBox.HotKey = null;

                DrawGestureTextBlock.Visibility = Visibility.Visible;
                RedrawButton.Visibility = Visibility.Collapsed;
                return await NamedPipe.SendMessageAsync("StartTeaching", "GestureSignDaemon");
            }
            else
            {
                DrawGestureTextBlock.Visibility = Visibility.Collapsed;
                RedrawButton.Visibility = Visibility.Visible;
                return await NamedPipe.SendMessageAsync("StopTraining", "GestureSignDaemon");
            }
        }

        private bool SaveGesture(string newName, PointPattern[] newPointPatterns)
        {
            if (string.IsNullOrEmpty(newName))
            {
                newName = GestureManager.GetNewGestureName();
            }

            var newGesture = new Gesture()
            {
                Name = newName,
                PointPatterns = newPointPatterns,
                MouseAction = (MouseActions?)MouseActionComboBox.SelectedValue ?? MouseActions.None,
                Hotkey = HotKeyTextBox.HotKey != null ?
                  new Hotkey()
                  {
                      KeyCode = KeyInterop.VirtualKeyFromKey(HotKeyTextBox.HotKey.Key),
                      ModifierKeys = (int)HotKeyTextBox.HotKey.ModifierKeys
                  } : null
            };

            if (GestureManager.Instance.GestureExists(newName))
            {
                GestureManager.Instance.DeleteGesture(newName);
            }
            GestureManager.Instance.AddGesture(newGesture);

            GestureManager.Instance.GestureName = newName;
            GestureManager.Instance.SaveGestures();

            return true;
        }

        #endregion
    }
}
