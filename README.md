# RFID Lock para Windows

El servicio `RfidLock` inicia con Windows y solicita que el sistema y la pantalla permanezcan activos mediante `SetThreadExecutionState`. Una tarea programada abre la aplicación en la sesión de Windows de la cuenta que realizó la instalación. La instalación deja la protección desactivada. El agente solo puede cubrir el escritorio cuando una configuración administrativa verificada haya habilitado la protección. Un gafete autorizado retira la cubierta y restaura la entrada. Tras cinco minutos sin actividad de entrada en esa sesión, vuelve a bloquearse. La aplicación no genera movimiento de mouse ni pulsaciones.

## Instalar

En Windows 10/11, instale el SDK de .NET 10 y abra PowerShell como administrador **con la misma cuenta que usará el equipo**. Para Windows ARM64, agregue `-Runtime win-arm64`. El ejecutable instalado incluye el runtime. La instalación requiere acceso a `https://api.nuget.org/v3/index.json` para restaurar los paquetes y el runtime.

Ejecute el instalador sin argumentos. El instalador abre la ventana de configuración inmediatamente y guarda una copia de cualquier configuración previa antes de registrar el inicio automático.

```powershell
.\Install.ps1
```

En **Administrador de dispositivos → Teclados**, identifique la lectora desconectándola y conectándola. Copie **Propiedades → Detalles → Ruta de instancia del dispositivo**. Pegue la ruta en la ventana de RFID Lock, indique la longitud del código antes de Enter y escriba el nombre del usuario. Pulse **Probar gafete** y escanee. La prueba activa los mismos filtros que usa el bloqueo por hasta ocho segundos. El botón lleva el foco al campo de solo lectura «Código leído por la lectora». Una lectura aceptada muestra el código en ese campo, registra el gafete y termina la prueba. Repita por cada usuario y pulse **Guardar y activar**. La configuración se guarda en `%LOCALAPPDATA%\RfidLock\config.json` de la cuenta que ejecuta la tarea, y el agente se activa en la sesión actual. Cerrar la ventana antes de guardar deja el escritorio accesible.

La configuración inicial admite lectoras USB HID que envían dígitos o letras A–Z seguidos de Enter. Al desbloquear, aparece un recuadro con el nombre del usuario, la cuenta regresiva y el botón **Bloquear**. El recuadro permanece visible mientras la sesión esté desbloqueada con un gafete. Cualquier interacción reinicia el plazo de cinco minutos. En modo COM, vuelva a escanear el gafete de la sesión para bloquearla. En modo HID, evite escanear con el escritorio desbloqueado y una consola enfocada, pues Windows puede enviar los caracteres y Enter a esa consola.

## PostgreSQL y diagnóstico

La configuración inicial incluye una cadena de conexión opcional y el botón **Probar conexión PostgreSQL**. Las credenciales se guardan cifradas para la cuenta de Windows que ejecuta la tarea. El bloqueo y la validación de gafetes usan el perfil local. La [migración](migrations/001_rfid_lock.sql) crea el esquema central y las [instrucciones](migrations/README.md) indican cómo aplicarla. La carga de CSV está pendiente.

Si un gafete desbloquea y el equipo vuelve a bloquearse de inmediato, consulte `%LOCALAPPDATA%\RfidLock\diagnostico.log`. El registro indica el motivo del bloqueo y si llegó una lectura HID válida, rechazada o no autorizada. Omite los códigos de gafete.

## Si la lectora se desconecta

La sesión permanece accesible mientras la lectora configurada no esté disponible. Si se desconecta durante el bloqueo, la aplicación retira la cubierta y restaura teclado y mouse. Al detectar nuevamente esa misma lectora, la aplicación vuelve a bloquearse. Esto aplica al arrancar y durante el uso.

## Recuperación

Este es un bloqueo de aplicación dentro de una sesión iniciada. La pantalla de inicio de sesión y el bloqueo nativo de Windows conservan su autenticación. Si falla la lectora, use Ctrl+Alt+Supr y cierre sesión desde la pantalla de seguridad. Un administrador también puede detener la tarea programada desde otra sesión. Las políticas de bloqueo de Windows siguen aplicándose y los identificadores RFID simples pueden copiarse.

Para retirar el servicio y la tarea, ejecute en PowerShell como administrador:

```powershell
.\Uninstall.ps1
```
