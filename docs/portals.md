# Setting up a captive portal login

SmartWiFi logs in to a portal the same way your browser does: it sends the login form. You just need to tell it
where the form goes and what fields it has. Most of the time a preset is enough.

## 1. Try a preset

In **Settings → Networks**, edit your hostel Wi-Fi, tick **Log in to a captive portal on this network**, and pick:

- **FortiGate (fgtauth)** if the login page's address looks like `http://10.x.x.x:1000/fgtauth?0123abcd…`
- **Pronto Networks** if it's on `phc.prontonetworks.com`

Enter your portal username and password, save, then use **Log in to portal now** from the tray menu while connected to that
network. The activity log says whether it worked.

## 2. Copy the form from your browser (Custom form)

If neither preset fits, or a preset stops working:

1. Connect to the hostel Wi-Fi and open any `http://` site (for example `http://neverssl.com`) so the portal page appears.
2. Press **F12** to open the developer tools and go to the **Network** tab. Tick **Preserve log**.
3. Log in normally.
4. In the list, find the request that was sent when you pressed the login button. It's usually a `POST`, often to something like
   `login`, `auth`, `authlogin` or the portal's root `/`.
5. Click it. Under **Headers**, note the **Request URL** and **Request Method**. Under **Payload** (or **Form Data**), note every
   field name and value.

Now in SmartWiFi choose **Custom form** and fill in:

| SmartWiFi field | From the browser |
|---|---|
| Login URL | Request URL |
| Method | Request Method |
| Form fields | One row per payload field |

Replace your actual username and password in the field values with `{username}` and `{password}`, so they come from the
encrypted fields instead of sitting in plain text.

### Placeholders

| Placeholder | Becomes |
|---|---|
| `{username}`, `{password}` | Your portal account (the password is stored encrypted) |
| `{portalUrl}` | The full address the portal redirected to |
| `{portalBase}` | Just `scheme://host:port` of that address, e.g. `http://10.10.0.1:1000` |
| `{magic}` | The token in the redirect: the bare query (`fgtauth?TOKEN`) or the `magic=` parameter |
| `{redirect}` | `http://www.msftconnecttest.com/redirect`, where to go after logging in |

A field that changes on every visit (a session token in the form) usually comes from the redirect; try `{magic}` for it. If it
only exists inside the HTML of the login page, that portal isn't supported yet. Please open an issue with the field names
(never your password).

## Tips

- **Self-signed HTTPS portal?** Tick *Accept the portal's self-signed certificate*. This only affects requests to the portal.
- **Logged out every few hours?** That's normal for many portals. SmartWiFi notices the portal again on the next check and logs
  back in.
- **Several networks, same portal?** Add each SSID separately with the same portal settings.
- **Checking it works:** tray → *Activity log…*. A good login looks like
  `on Hostel-WiFi without internet, logging in to the portal` followed by `portal: logged in, online`.
