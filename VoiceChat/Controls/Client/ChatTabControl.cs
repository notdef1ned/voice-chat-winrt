﻿using System.Windows.Controls;
using Backend.Helper;

namespace Controls.Client
{
    public class ChatTabControl : TabControl
    {
        public ChatTabItem GlobalPage { get; set; }

        public ChatTabControl()
        {
            GlobalPage = new ChatTabItem {Header = ChatHelper.Global};
            Items.Add(GlobalPage);
        }
    }
}