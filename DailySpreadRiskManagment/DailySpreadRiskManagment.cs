using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using System.Collections.Generic;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class DailySpreadRiskManagement : Robot
    {
        [Parameter("Start Hour", DefaultValue = 21)]
        public int StartHour { get; set; }

        [Parameter("Start Minute", DefaultValue = 0)]
        public int StartMinute { get; set; }

        [Parameter("End Hour", DefaultValue = 22)]
        public int EndHour { get; set; }

        [Parameter("End Minute", DefaultValue = 0)]
        public int EndMinute { get; set; }

        [Parameter("Percent of Account to Risk per Order", DefaultValue = 10, MinValue = 0, MaxValue = 100)]
        public double PercentRisk { get; set; }

        [Parameter("Tick Threshold", DefaultValue = 10, MinValue = 1)]
        public int TickThreshold { get; set; }

        // Struct to store pending order details
        public class PendingOrder
        {
            public int Id { get; set; }
            public PendingOrderType OrderType { get; set; }
            public TradeType TradeType { get; set; }
            public string SymbolName { get; set; }
            public double VolumeInUnits { get; set; }
            public double TargetPrice { get; set; }
            public double? StopLossPips { get; set; }
        }

        // Struct to store stop loss data
        public class PositionWithStopLoss
        {
            public int PositionId { get; set; }  
            public double? OriginalStopLossPips { get; set; }
        }

        private List<PendingOrder> _pendingOrders;
        private List<PositionWithStopLoss> _positionsWithStopLosses;
        public bool Check = true;

        // Dictionary to track consecutive drawdown ticks for each position
        private Dictionary<int, int> drawdownTickCounter = new Dictionary<int, int>();

        protected override void OnStart()
        {
            _pendingOrders = new List<PendingOrder>();
            _positionsWithStopLosses = new List<PositionWithStopLoss>();

            Print("Robot started at ", Server.Time.ToString());
            Print("Account Balance: ", Account.Balance);
        }

        protected override void OnTick()
        {
            DateTime currentTime = Server.Time;
            DateTime startTime = new DateTime(currentTime.Year, currentTime.Month, currentTime.Day, StartHour, StartMinute, 0);
            DateTime endTime = new DateTime(currentTime.Year, currentTime.Month, currentTime.Day, EndHour, EndMinute, 0);

            if (currentTime >= startTime && currentTime < endTime && Check)
            {
                RemoveStopLossesFromPositions();
                BackupAndCancelPendingOrders();
                Check = false;
            }
            else if (!Check && currentTime > endTime)
            {
                RestorePendingOrders();
                ReApplyStopLossesToPositions();
                _pendingOrders.Clear();
                _positionsWithStopLosses.Clear();
                Check = true;
            }

            MonitorTradesForDrawdown();
        }

        // Method to remove stop losses by setting stop loss pips to null
        private void RemoveStopLossesFromPositions()
        {
            foreach (var position in Positions)
            {
                if (position.StopLoss.HasValue)
                {
                    // Calculate stop loss pips
                    double stopLossPips = CalculateStopLossPips(position);
                    
                    // Store the stop loss pips for later re-application
                    _positionsWithStopLosses.Add(new PositionWithStopLoss
                    {
                        PositionId = position.Id,
                        OriginalStopLossPips = stopLossPips
                    });

                    Print("Stored Stop Loss Pips for position ", position.Id, ": ", stopLossPips);

                    // Remove stop loss by setting pips to null
                    position.ModifyStopLossPips(null);
                    Print("Removed Stop Loss from position: ", position.Id);
                }
            }
        }

        // Method to calculate stop loss in pips
        private double CalculateStopLossPips(Position position)
        {
            double stopLossPrice = position.StopLoss.Value;
            double entryPrice = position.EntryPrice;
            double pipSize = Symbol.PipSize;
            double stopLossPips;

            if (position.TradeType == TradeType.Buy)
            {
                stopLossPips = (entryPrice - stopLossPrice) / pipSize;
            }
            else
            {
                stopLossPips = (stopLossPrice - entryPrice) / pipSize;
            }

            return stopLossPips;
        }

        private void BackupAndCancelPendingOrders()
        {
            foreach (var pendingOrder in PendingOrders.ToList())
            {
                _pendingOrders.Add(new PendingOrder
                {
                    Id = pendingOrder.Id,
                    OrderType = pendingOrder.OrderType,
                    TradeType = pendingOrder.TradeType,
                    SymbolName = pendingOrder.SymbolName,
                    VolumeInUnits = pendingOrder.VolumeInUnits,
                    TargetPrice = pendingOrder.TargetPrice,
                    StopLossPips = pendingOrder.StopLossPips
                });

                var orderToCancel = PendingOrders.FirstOrDefault(o => o.Id == pendingOrder.Id);
                if (orderToCancel != null)
                {
                    CancelPendingOrder(orderToCancel);
                    Print("Canceled pending order: ", orderToCancel.Id);
                }
            }
        }

        private void RestorePendingOrders()
        {
            foreach (var order in _pendingOrders)
            {
                switch (order.OrderType)
                {
                    case PendingOrderType.Limit:
                        PlaceLimitOrder(order.TradeType, order.SymbolName, order.VolumeInUnits, order.TargetPrice);
                        Print("Restored Limit order for symbol: ", order.SymbolName);
                        break;

                    case PendingOrderType.Stop:
                        PlaceStopOrder(order.TradeType, order.SymbolName, order.VolumeInUnits, order.TargetPrice);
                        Print("Restored Stop order for symbol: ", order.SymbolName);
                        break;
                }
            }
        }

        // Method to re-apply stop losses after the specified time
        private void ReApplyStopLossesToPositions()
        {
            foreach (var positionWithStopLoss in _positionsWithStopLosses)
            {
                var position = Positions.FirstOrDefault(p => p.Id == positionWithStopLoss.PositionId);
                if (position != null && positionWithStopLoss.OriginalStopLossPips.HasValue)
                {
                    // Re-apply the original stop loss using ModifyStopLossPips
                    position.ModifyStopLossPips(positionWithStopLoss.OriginalStopLossPips.Value);
                    Print("Re-applied Stop Loss Pips for position: ", position.Id, ": ", positionWithStopLoss.OriginalStopLossPips.Value);
                }
            }
        }

        private void MonitorTradesForDrawdown()
        {
            double accountBalance = Account.Balance;
            double maxDrawdownAmount = (PercentRisk / 100) * accountBalance;

            foreach (var position in Positions)
            {
                double currentDrawdown = position.GrossProfit;
                int positionId = position.Id;

                if (currentDrawdown < 0 && Math.Abs(currentDrawdown) >= maxDrawdownAmount)
                {
                    if (drawdownTickCounter.ContainsKey(positionId))
                    {
                        drawdownTickCounter[positionId]++;
                    }
                    else
                    {
                        drawdownTickCounter[positionId] = 1;
                    }

                    if (drawdownTickCounter[positionId] >= TickThreshold)
                    {
                        ClosePosition(position);
                        Print("Closed position due to exceeding drawdown for tick threshold: ", positionId);
                        drawdownTickCounter.Remove(positionId);
                    }
                }
                else
                {
                    if (drawdownTickCounter.ContainsKey(positionId))
                    {
                        drawdownTickCounter.Remove(positionId);
                    }
                }
            }
        }

        protected override void OnStop()
        {
            Print("Robot stopped at ", Server.Time.ToString());
        }
    }
}
