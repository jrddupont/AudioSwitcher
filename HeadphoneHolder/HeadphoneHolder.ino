#include <Adafruit_DotStar.h>

// Messages that this device and the computer recognizes as commands
#define MESSAGE_SCAN_QUERY "Are you a headphone holder?"
#define MESSAGE_SCAN_RESPONSE "Yes I am!"
#define MESSAGE_DATA_PROMPT "Docked?"
#define MESSAGE_YES "Yes!"
#define MESSAGE_NO "No!"

#define NEWLINE '\n'

// Ground on one side, pin 3 on the other
#define SENSOR_PIN A3

// Pins to control the LED on the front of the device
#define NUMPIXELS 1
#define BRIGHTNESS 8
#define DATAPIN 7
#define CLOCKPIN 8

Adafruit_DotStar strip = Adafruit_DotStar(NUMPIXELS, DATAPIN, CLOCKPIN, DOTSTAR_BGR);

void setup() {
  pinMode(SENSOR_PIN, INPUT); 
  digitalWrite(SENSOR_PIN, HIGH); // Enable pullup resistor
  strip.begin(); // Initialize pins for output
  strip.show();  // Turn all LEDs off ASAP
  Serial.begin(9600);
  Serial.setTimeout(250);
}

String message = "";
void loop() {

  // Just wait forever until we get a message from the computer
  message = "";
  message = Serial.readStringUntil(NEWLINE);
  message.trim();
  
  if(message.equals(MESSAGE_SCAN_QUERY)){
    // This is a simple response that the computer uses to check if this is the right device 
    Serial.println(MESSAGE_SCAN_RESPONSE);
  } else if(message.equals(MESSAGE_DATA_PROMPT)){
    // This is the response that actually checks if the headphones are docked or not 
    if( digitalRead(SENSOR_PIN) == HIGH){
      Serial.println(MESSAGE_YES);
    } else {
      Serial.println(MESSAGE_NO);
    }
  }
}
