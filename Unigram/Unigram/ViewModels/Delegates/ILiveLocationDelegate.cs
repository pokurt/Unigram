﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Td.Api;

namespace Unigram.ViewModels.Delegates
{
    public interface ILiveLocationDelegate : IViewModelDelegate
    {
        void UpdateNewMessage(Message message);
        void UpdateMessageContent(Message message);
    }
}
