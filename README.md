# RFID Lock para Windows

El servicio `RfidLock` inicia con Windows y solicita que el sistema y la pantalla permanezcan activos mediante `SetThreadExecutionState`. Una tarea programada abre la aplicación en la sesión de Windows de la cuenta que realizó la instalación. La aplicación inicia bloqueada, cubre todos los monitores y filtra el teclado y el mouse en el escritorio de la sesión. Un gafete autorizado retira la cubierta y restaura la entrada. Tras cinco minutos sin actividad de entrada en esa sesión, vuelve a bloquearse. La aplicación no genera movimiento de mouse ni pulsaciones.

## Instalar

En Windows 10/11, instale el SDK de .NET 10 y abra PowerShell como administrador **con la misma cuenta que usará el equipo**. Para Windows ARM64, agregue `-Runtime win-arm64`. El ejecutable instalado incluye el runtime. La instalación requiere acceso a `https://api.nuget.org/v3/index.json` para restaurar los paquetes y el runtime.

### Lectora USB en modo teclado (HID)

En el Administrador de dispositivos, desconecte y conecte la lectora para identificar el dispositivo que aparece en **Teclados**. Copie **Propiedades → Detalles → Ruta de instancia del dispositivo**. El instalador verifica que esa instancia esté conectada y guarda la ruta en la configuración local. Use la ruta de cada máquina. Si Windows asigna otra instancia al cambiar de puerto USB o de lectora, vuelva a configurar la ruta.

```powershell
.\Install.ps1 -HidInstance '<RUTA_DE_INSTANCIA_HID>' -BadgeLength 10 -Badges @('0000123456', '0000567890')
```

Sustituya los números de ejemplo por los gafetes autorizados, incluidos los ceros iniciales. El modo HID admite identificadores de longitud fija con dígitos o letras A–Z seguidos de Enter. Los códigos alfabéticos se interpretan en mayúsculas. El teclado físico queda excluido por la ruta de instancia.

### Lectora con puerto COM

La lectora debe enviar identificadores ASCII terminados por CR, LF o CRLF. Consulte el puerto en **Puertos (COM y LPT)** y cierre cualquier programa que lo esté usando. Si se desconecta la lectora, la aplicación reintenta abrir el puerto.

```powershell
.\Install.ps1 -Port COM3 -BaudRate 9600 -Badges @('00001234', '00005678')
```

Para cambiar la configuración de una instalación existente, desinstale y vuelva a instalar con los parámetros nuevos.

Cierre sesión e iníciela de nuevo para probar. En modo HID, teclee manualmente un identificador autorizado seguido de Enter y confirme que el bloqueo sigue activo. Compruebe que un gafete desconocido conserva el bloqueo, que uno autorizado restaura teclado y mouse, y que tras cinco minutos sin tocar el equipo reaparece la cubierta. Reinicie para comprobar el inicio del servicio y la activación del bloqueo al volver a iniciar sesión.

## Recuperación

Este es un bloqueo de aplicación dentro de una sesión iniciada. La pantalla de inicio de sesión y el bloqueo nativo de Windows conservan su autenticación. Si falla la lectora, use Ctrl+Alt+Supr y cierre sesión desde la pantalla de seguridad. Un administrador también puede detener la tarea programada desde otra sesión. Las políticas de bloqueo de Windows siguen aplicándose y los identificadores RFID simples pueden copiarse.

Para retirar el servicio y la tarea, ejecute en PowerShell como administrador:

```powershell
.\Uninstall.ps1
```
