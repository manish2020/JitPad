﻿using JitPad.Foundation;

namespace JitPad.Core
{
    public class SourceFile : NotificationObject
    {
        #region FilePath
        
        private string _FilePath = "";
        
        public string FilePath
        {
            get => _FilePath;
            set => SetProperty(ref _FilePath, value);
        }
        
        #endregion

        #region Text
        
        private string _Text = "";
        
        public string Text
        {
            get => _Text;
            set => SetProperty(ref _Text, value);
        }
        
        #endregion
    }
}