<p>
Инструкция по установке<br>
Для работы Ts4 write key требуется<br>
1 добавить процедуру cardindev_ts4 <br>
1.1 исполните sql скрипт cardindev_ts4.sql <br>
2 изменить ThreadingModel COM обьекта подробнне: https://learn.microsoft.com/en-us/windows/win32/com/inprocserver32 <br>
2.1 Зайти в реестр <br>
2.2 найти COM обьект по его CLSID - EAE30322-9FA6-4466-B3AE-DFB1D58813D3 <br>
Примерный путь: (Компьютер\HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\CLSID\{EAE30322-9FA6-4466-B3AE-DFB1D58813D3}\InprocServer32) <br>
2.2 изменить параметр ThreadingModel=Apartment <br>
3 Настроить программу appsettings.json <br>
4 запустить программу убедится в её корректной работе <br>
5 Зарегистрировать как службу. <br>
5.1 sc.exe create ts4w binpath=(путь до exe программы) <br>        
<br>
git https://github.com/Lexer25/ts4 <br>
02.04.2025 Евскин И.В. http://artonit.ru/ 
</p>