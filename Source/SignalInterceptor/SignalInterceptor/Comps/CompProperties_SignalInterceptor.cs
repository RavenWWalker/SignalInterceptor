using Verse;

namespace SignalInterceptor
{
    public class CompProperties_SignalInterceptor : CompProperties
    {
        // Среднее время между находками (в игровых днях), если бы пешка работала непрерывно
        public float scanFindMtbDays = 5f;

        // Гарантированная находка после этого количества дней работы
        public float scanFindGuaranteedDays = 12f;

        // Сколько тиков длится один сеанс сканирования (2500 тиков = 1 игровой час)
        public int scanDurationTicks = 5000;

        public CompProperties_SignalInterceptor()
        {
            this.compClass = typeof(CompSignalInterceptor);
        }
    }
}
