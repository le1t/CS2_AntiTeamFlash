### CS2_AntiTeamFlash
Плагин предотвращает ослепление игроков флешками от своих же (тиммейтов). При попытке ослепить союзника ослепление блокируется, а в HUD отображается статистика по данной флешке: количество ослеплённых противников и тиммейтов, команда атакующего (красный для T, синий для CT). Статистика собирается в течение заданного времени (FlashAggregationTime) для каждой отдельной флешки.

~~~
### Требования
CounterStrikeSharp API версии 362 или выше
.NET 8.0 Runtime
~~~
~~~
Конфигурационные параметры
css_antiteamflash_enabled <0/1>, def.=1 – Включение/выключение плагина.
css_antiteamflash_flashowner <0/1>, def.=1 – Разрешить самоослепление (собственной флешкой): 1 – разрешено, 0 – блокировать самоослепление.
css_antiteamflash_loglevel <0-5>, def.=4 – Уровень логирования (0-Trace,1-Debug,2-Info,3-Warning,4-Error,5-Critical).
css_antiteamflash_hud_duration <1.0-10.0>, def.=3.0 – Длительность показа сообщений в HUD (секунды).
css_antiteamflash_flash_aggregation_time <1.0-10.0>, def.=3.0 – Время агрегации статистики для одной флешки (секунды). За этот период собираются все ослеплённые от одной гранаты, после чего статистика сбрасывается.
~~~
~~~
Консольные команды
css_antiteamflash_help – Показать подробную справку по плагину.
css_antiteamflash_settings – Показать текущие настройки и активные флешки.
css_antiteamflash_test – Вывести в чат информацию о настройках и отправить тестовое сообщение в HUD (доступно только игроку).
css_antiteamflash_reload – Перезагрузить конфигурацию из файла и сбросить все активные данные.
css_antiteamflash_setenabled <0/1> – Установить значение css_antiteamflash_enabled.
css_antiteamflash_setflashowner <0/1> – Установить значение css_antiteamflash_flashowner.
css_antiteamflash_setloglevel <0-5> – Установить уровень логирования.
css_antiteamflash_sethudduration <1.0-10.0> – Установить css_antiteamflash_hud_duration.
css_antiteamflash_setaggregationtime <1.0-10.0> – Установить css_antiteamflash_flash_aggregation_time.
~~~
~~~
ЭТОТ ПЛАГИН ФОРК ЭТОГО ПЛАГИНА:
https://github.com/Jesewe/cs2-noflash
~~~
